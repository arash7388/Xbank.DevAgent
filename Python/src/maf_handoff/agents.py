from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from agent_framework import Agent

from . import prompts


@dataclass(frozen=True, slots=True)
class AgentTeam:
    intake: Agent
    planner: Agent
    developer: Agent
    reviewer: Agent
    security: Agent
    release_manager: Agent

    @property
    def participants(self) -> list[Agent]:
        return [
            self.intake,
            self.planner,
            self.developer,
            self.reviewer,
            self.security,
            self.release_manager,
        ]


def _agent(client: Any, *, name: str, description: str, instructions: str) -> Agent:
    return Agent(
        client=client,
        id=f"software-delivery-{name}",
        name=name,
        description=description,
        instructions=instructions,
        require_per_service_call_history_persistence=True,
    )


def create_agent_team(client: Any) -> AgentTeam:
    """Create agents with stable IDs and names for handoff checkpoint rehydration."""
    return AgentTeam(
        intake=_agent(
            client,
            name="intake",
            description="Intake, constraint capture, and initial routing.",
            instructions=prompts.INTAKE,
        ),
        planner=_agent(
            client,
            name="planner",
            description="Requirements decomposition and software architecture.",
            instructions=prompts.PLANNER,
        ),
        developer=_agent(
            client,
            name="developer",
            description="Implementation, tests, and remediation.",
            instructions=prompts.DEVELOPER,
        ),
        reviewer=_agent(
            client,
            name="reviewer",
            description="Independent correctness and quality review.",
            instructions=prompts.REVIEWER,
        ),
        security=_agent(
            client,
            name="security",
            description="Threat modeling and application security review.",
            instructions=prompts.SECURITY,
        ),
        release_manager=_agent(
            client,
            name="release_manager",
            description="Final evidence gate and delivery summary.",
            instructions=prompts.RELEASE_MANAGER,
        ),
    )
