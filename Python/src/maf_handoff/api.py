from __future__ import annotations

import json
from collections.abc import AsyncIterator

from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, Field

from .config import ConfigurationError, Settings
from .runtime import RunEvent, stream_task

app = FastAPI(
    title="MAF Handoff Workflow",
    version="0.1.0",
    description="A gated software-delivery agent team built on Microsoft Agent Framework.",
)


class RunRequest(BaseModel):
    task: str = Field(min_length=1, max_length=50_000)


class RunResponse(BaseModel):
    events: list[dict[str, str | None]]


def _settings() -> Settings:
    try:
        return Settings.from_env()
    except ConfigurationError as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/runs", response_model=RunResponse)
async def run(request: RunRequest) -> RunResponse:
    events = [event.to_dict() async for event in stream_task(request.task, _settings())]
    return RunResponse(events=events)


@app.post("/runs/stream")
async def run_stream(request: RunRequest) -> StreamingResponse:
    settings = _settings()

    async def generate() -> AsyncIterator[str]:
        try:
            async for event in stream_task(request.task, settings):
                yield _sse(event)
            yield "event: done\ndata: {}\n\n"
        except Exception as exc:  # The stream has already started, so report in-band.
            payload = json.dumps({"error": type(exc).__name__, "message": str(exc)})
            yield f"event: error\ndata: {payload}\n\n"

    return StreamingResponse(generate(), media_type="text/event-stream")


def _sse(event: RunEvent) -> str:
    return f"event: {event.type}\ndata: {json.dumps(event.to_dict())}\n\n"

