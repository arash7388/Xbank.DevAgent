# Microsoft Agent Framework Handoff Workflow

A gated software-delivery agent team built on Microsoft Agent Framework. It uses handoff
orchestration rather than treating specialists as tools: the active agent transfers task ownership
and the receiving agent gets the full conversation context.

See [the architecture record](docs/architecture.md) for lifecycle, trust boundaries, and production
extension points.

## Topology

```text
User -> Intake -> Planner -> Developer -> Reviewer -> Security -> Release Manager -> Done
                                  ^    |           |              |
                                  |    +-----------+--------------+
                                  +---------- remediation --------+
```

The allowed edges enforce separation of duties:

| Agent | Responsibility | May hand off to |
| --- | --- | --- |
| `intake` | Intake and constraint capture | `planner` |
| `planner` | Architecture and acceptance criteria | `developer` |
| `developer` | Implementation, tests, remediation | `reviewer` |
| `reviewer` | Correctness and quality gate | `developer`, `security` |
| `security` | Threat model and security gate | `developer`, `release_manager` |
| `release_manager` | Evidence check and final response | `planner`, `developer` |

Only the release manager can end a run by emitting `WORKFLOW_COMPLETE`. A total-message ceiling and
per-agent autonomous-turn limits prevent unbounded loops.

## Prerequisites

- Python 3.10 or newer (the container uses Python 3.13)
- A Microsoft Foundry project and deployed model
- Azure CLI authentication for local development (`az login`), or managed identity in Azure

## Run locally

```powershell
Copy-Item .env.example .env
# Fill in FOUNDRY_PROJECT_ENDPOINT and FOUNDRY_MODEL in .env
py -m venv .venv
.venv\Scripts\python -m pip install -e ".[dev]"
.venv\Scripts\python -m pytest
.venv\Scripts\python -m uvicorn maf_handoff.api:app --reload
```

Open `http://127.0.0.1:8000/docs`, or run the CLI:

```powershell
.venv\Scripts\maf-handoff "Design a rate-limited webhook ingestion service"
```

Start a streamed run:

```bash
curl -N -X POST http://127.0.0.1:8000/runs/stream \
  -H "Content-Type: application/json" \
  -d '{"task":"Design a rate-limited webhook ingestion service"}'
```

## Production notes

- Use `AZURE_CREDENTIAL=managed_identity` in Azure. `AzureCliCredential` is intentionally limited to
  local development instead of using a broad fallback credential chain.
- Put authentication, authorization, request quotas, and a durable run store in front of the API
  before exposing it outside a trusted environment.
- Handoff agents execute model-selected local tools. This starter does not grant filesystem, shell,
  network, or deployment tools; add narrow tools with approval for consequential actions.
- Keep agent names stable when adding checkpoint storage because handoff routing and checkpoint
  rehydration depend on participant identity.

## Why handoff

Handoff fits work where ownership should move between specialists and each specialist needs the full
context. The graph makes review and security explicit gates. For embarrassingly parallel work, add a
nested concurrent workflow; for centrally decomposed subtasks that always return to one controller,
agent-as-tools is a better fit.

## References

- [Microsoft Agent Framework handoff orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff)
- [Microsoft Agent Framework Python orchestration samples](https://github.com/microsoft/agent-framework/tree/main/python/samples/03-workflows/orchestrations)
- [Autonomous handoff sample](https://github.com/microsoft/agent-framework/blob/main/python/samples/03-workflows/orchestrations/handoff_autonomous.py)
