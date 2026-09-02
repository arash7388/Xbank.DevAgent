from __future__ import annotations

from typing import Any

from agent_framework.orchestrations import HandoffBuilder

from .agents import AgentTeam, create_agent_team
from .config import Settings

COMPLETION_MARKER = "WORKFLOW_COMPLETE"


def is_complete(conversation: list[Any], *, max_messages: int = 40) -> bool:
    if len(conversation) >= max_messages:
        return True
    return any(
        getattr(message, "author_name", None) == "release_manager"
        and COMPLETION_MARKER in (getattr(message, "text", "") or "")
        for message in conversation
    )


def build_workflow(client: Any, settings: Settings) -> tuple[Any, AgentTeam]:
    team = create_agent_team(client)
    workflow = (
        HandoffBuilder(
            name="software_delivery_handoff",
            participants=team.participants,
            termination_condition=lambda conversation: is_complete(
                conversation, max_messages=settings.max_total_messages
            ),
        )
        .with_start_agent(team.intake)
        .add_handoff(team.intake, [team.planner])
        .add_handoff(team.planner, [team.developer])
        .add_handoff(team.developer, [team.reviewer])
        .add_handoff(team.reviewer, [team.developer, team.security])
        .add_handoff(team.security, [team.developer, team.release_manager])
        .add_handoff(team.release_manager, [team.planner, team.developer])
        .with_autonomous_mode(
            turn_limits={agent.name: settings.max_agent_turns for agent in team.participants}
        )
        .build()
    )
    return workflow, team


def topology(team: AgentTeam) -> dict[str, tuple[str, ...]]:
    """Return the intended graph for documentation and structural tests."""
    return {
        team.intake.name: (team.planner.name,),
        team.planner.name: (team.developer.name,),
        team.developer.name: (team.reviewer.name,),
        team.reviewer.name: (team.developer.name, team.security.name),
        team.security.name: (team.developer.name, team.release_manager.name),
        team.release_manager.name: (team.planner.name, team.developer.name),
    }
