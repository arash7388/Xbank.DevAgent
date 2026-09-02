from __future__ import annotations

from collections.abc import AsyncIterator
from dataclasses import asdict, dataclass
from typing import Any

from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential, ManagedIdentityCredential

from .config import Settings
from .workflow import COMPLETION_MARKER, build_workflow


@dataclass(frozen=True, slots=True)
class RunEvent:
    type: str
    agent: str | None = None
    text: str | None = None
    source: str | None = None
    target: str | None = None
    status: str | None = None

    def to_dict(self) -> dict[str, str | None]:
        return asdict(self)


def create_client(settings: Settings) -> FoundryChatClient:
    if settings.credential_mode == "managed_identity":
        credential = ManagedIdentityCredential(client_id=settings.managed_identity_client_id)
    else:
        credential = AzureCliCredential()
    return FoundryChatClient(
        project_endpoint=settings.foundry_project_endpoint,
        model=settings.foundry_model,
        credential=credential,
    )


def _messages_from_output(data: Any) -> list[RunEvent]:
    if not isinstance(data, list):
        text = getattr(data, "text", None)
        if text:
            return [RunEvent(type="message", agent=getattr(data, "author_name", None), text=text)]
        return []

    events: list[RunEvent] = []
    for message in data:
        text = getattr(message, "text", None)
        if text:
            events.append(
                RunEvent(
                    type="message",
                    agent=getattr(message, "author_name", None)
                    or str(getattr(message, "role", "")),
                    text=text,
                )
            )
    return events


async def stream_task(task: str, settings: Settings) -> AsyncIterator[RunEvent]:
    client = create_client(settings)
    workflow, _ = build_workflow(client, settings)
    final_status = "stopped_without_release_approval"
    async for event in workflow.run(task, stream=True):
        if event.type == "handoff_sent":
            yield RunEvent(
                type="handoff",
                source=str(event.data.source),
                target=str(event.data.target),
            )
        elif event.type == "output":
            if isinstance(event.data, list):
                release_completed = any(
                    getattr(message, "author_name", None) == "release_manager"
                    and COMPLETION_MARKER in (getattr(message, "text", "") or "")
                    for message in event.data
                )
                if release_completed:
                    final_status = "completed"
                elif len(event.data) >= settings.max_total_messages:
                    final_status = "message_limit_reached"
            for output in _messages_from_output(event.data):
                yield output
    yield RunEvent(type="run_finished", status=final_status)
