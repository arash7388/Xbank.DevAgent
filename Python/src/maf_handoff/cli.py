from __future__ import annotations

import argparse
import asyncio
import json
import sys

from .config import ConfigurationError, Settings
from .runtime import stream_task


async def _run(task: str) -> int:
    try:
        settings = Settings.from_env()
        async for event in stream_task(task, settings):
            if event.type == "handoff":
                print(f"[{event.source} -> {event.target}]", file=sys.stderr)
            elif event.type == "run_finished":
                print(f"[status: {event.status}]", file=sys.stderr)
            elif event.text:
                print(json.dumps({"agent": event.agent, "text": event.text}))
        return 0
    except ConfigurationError as exc:
        print(f"Configuration error: {exc}", file=sys.stderr)
        return 2


def main() -> None:
    parser = argparse.ArgumentParser(description="Run the Microsoft Agent Framework handoff team")
    parser.add_argument("task", help="Software-delivery task for the agent team")
    args = parser.parse_args()
    raise SystemExit(asyncio.run(_run(args.task)))


if __name__ == "__main__":
    main()
