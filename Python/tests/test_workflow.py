from dataclasses import dataclass
from typing import Any

from maf_handoff.config import Settings
from maf_handoff.workflow import COMPLETION_MARKER, build_workflow, is_complete, topology


@dataclass
class StubMessage:
    author_name: str
    text: str


def test_only_release_manager_can_complete() -> None:
    conversation = [StubMessage("developer", COMPLETION_MARKER)]

    assert not is_complete(conversation)


def test_release_marker_completes_workflow() -> None:
    conversation = [StubMessage("release_manager", f"Ready\n{COMPLETION_MARKER}")]

    assert is_complete(conversation)


def test_message_cap_stops_runaway_workflow() -> None:
    conversation = [StubMessage("developer", "working") for _ in range(3)]

    assert is_complete(conversation, max_messages=3)


class StubChatClient:
    async def get_response(self, *_args: Any, **_kwargs: Any) -> Any:
        raise AssertionError("A structural test must not call the model")


def test_real_handoff_builder_accepts_gated_topology() -> None:
    settings = Settings(foundry_project_endpoint="unused", foundry_model="unused")

    workflow, team = build_workflow(StubChatClient(), settings)

    assert workflow is not None
    assert topology(team) == {
        "intake": ("planner",),
        "planner": ("developer",),
        "developer": ("reviewer",),
        "reviewer": ("developer", "security"),
        "security": ("developer", "release_manager"),
        "release_manager": ("planner", "developer"),
    }
    assert len({agent.id for agent in team.participants}) == len(team.participants)
