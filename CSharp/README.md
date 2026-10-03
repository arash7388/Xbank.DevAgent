# Repository Change Workbench

A .NET 10 sample that connects Microsoft Agent Framework 1.19 handoff agents to a GitHub or GitLab
repository. The agents inspect and edit an isolated clone, then the browser shows the exact Git diff
and requires user confirmation before any commit or push.

No Azure services or Azure packages are used. The workbench supports OpenAI through Chat
Completions, DeepSeek through its provider-native Microsoft Agent Framework adapter, and Claude
through Anthropic's official C# `IChatClient` integration.

Provider definitions include model identity, URL, tool-calling/vision/thinking capabilities, token
limits, and supported reasoning-effort values. Full `/chat/completions` URLs are normalized to the
SDK base endpoint. DeepSeek agents preserve the selected reasoning level without returning its full trace;
OpenAI reasoning values are passed through its Chat Completions options. Tool calling is mandatory
for this handoff topology.

For repositories containing tracked C# source, planner, developer, reviewer, and security agents can
also receive the read-only `find_symbol`, `get_context`, `get_context_by_id`, and `get_neighbors` tools.
CodeGraph is complementary: agents use a focused query when it can make analysis faster or cheaper,
then continue with targeted repository evidence when the graph has no useful match. Missing, stale, or
unavailable graph data never blocks a handoff or the workflow. Configuration, documentation, and other
non-C# work normally needs no graph call.

Each agent has an independent OpenAI, DeepSeek, or Claude provider selector, model selector, and
reasoning-effort selector. All agents default to DeepSeek `deepseek-flash` with low reasoning,
but can be configured individually. Provider credentials are entered once and reused only by agents
assigned to that provider. Endpoints and capability metadata remain internal.

Before starting a run, **Review & confirm** shows the full change request, repository and service
settings, enabled and disabled agents, model selections, reasoning, guidance, token pricing, and
selected reference and Jira attachments. Credentials appear only as configured or missing.
Use **Edit** to return to a setup step; returning to review rebuilds the summary from the current
values. Documents are indexed and the session starts only after **Confirm and start agent run**.

Jira and security are optional and start disabled. Every run uses Planner to produce a
repository-backed implementation contract before Developer changes the repository. When Jira is enabled,
it supplies issue context to Planner first. Planner, Developer, Reviewer, and Release Manager are required.
Reviewer independently inspects and confirms every change before release. Completion requires a
reviewer-issued `REVIEW_APPROVED` after the latest Developer work; missing approval stops the run with
`review_approval_missing`. Enable Security when the work's
context or risk needs it. A run allows at most 25 model
requests by default (`MAF_MAX_LLM_REQUESTS`), including checkpoint replays. Each agent also has a hard
limit of 25 model turns (`MAF_MAX_AGENT_TURNS`), counting its initial request, tool rounds, continuations,
and later activations together. These limits apply even to workflows created directly through the
factory. The next request stops with `request_limit_reached` or `agent_turn_limit_reached` before
calling the provider or replaying a checkpoint. Provider usage totals still exclude checkpoint hits.
Agents retain tool evidence throughout their current turn and must read tool results before handing off.
Mixed tool/handoff responses execute the ordinary tools first and ask the model to reconsider the handoff
with those results. Reviewer returns are counted from assistant messages only, so tool-result authors cannot
hide remediation cycles. Reviews should group all concrete blockers and verify prior fixes without expanding scope.
A shared activation guard also enforces `MAF_MAX_REVIEWER_REMEDIATION_CYCLES` (default five) before
another developer provider call, including handoff-only turns that bypass transcript termination checks.
Exhausting this budget stops the run with `reviewer_remediation_limit_reached`, without approving the patch.
Only a completed, tool-free Release Manager response can approve the run. Limit stops and rejection
take precedence over conflicting approval markers; user and tool messages cannot terminate a run.
When Planner is enabled, it receives at most three ordinary repository-tool rounds; its next request is
restricted to the Developer handoff so repeated inspection cannot consume the global request budget.

At the end of every run, the activity view and session response show provider-reported input/output
tokens for every participating agent and the run total. To calculate an estimated USD cost, enter the
current cached-input, uncached-input, and output price per million tokens for each agent model in
**Usage pricing**. DeepSeek rates are refreshed from the provider's pricing page at page load and when
continuing from Request & references to Review & confirm for the current UTC price band; these values remain editable.
The confirmed run uses the displayed rates without refreshing them after confirmation. The price-band schedule includes
weekends and the official 2026 Chinese public holidays (the holiday calendar needs updating annually).
If refresh fails, the UI warns that displayed rates may be outdated. DeepSeek cache-hit input is preserved
from the raw API response and billed separately from uncached input. Reasoning is included in output
tokens and is not charged twice. Runs use their starting rates, so costs remain estimates if a run crosses
a pricing-band boundary. When a rate or provider
usage metadata is unavailable, the workbench reports the token count without inventing a dollar amount.

Completed LLM calls are checkpointed in a separate SQLite database and reused when the agent, model,
instructions, tool definitions, model options, and complete conversation input are identical. This
allows a restarted workflow to skip calls completed before an exception. Running and failed calls are
retained for diagnosis but never reused; only fully saved responses are cache hits. The activity stream
reports checkpoint hits and actual provider calls, and token usage counts only calls sent to a provider.
Each checkpoint row includes a `workflow_id`, allowing all provider-call attempts from the same workflow run
to be queried together. Actual local tool executions are stored separately in `tool_calls`, including the
caller agent, tool name, input JSON, response or error JSON, status, duration, timestamps, and the same
`workflow_id`. Framework handoffs are not tool executions and are therefore not inserted into this table.

The same database also keeps an audit record for every repository workflow. `workflows` contains the
immutable, versioned input/configuration snapshot, while `workflow_runs` contains execution status and
timestamps. Secret values (provider API keys and source-control tokens) are never written to the snapshot; only a
boolean indicating whether each credential was configured is retained. The session ID returned by the API
is the `workflow_runs.id`, so checkpoints and tool calls can be correlated with the recorded run.

Every outbound model call is also saved in `ai_calls` before contacting the provider. Each record keeps
the agent/model/endpoint, request and response JSON, provider response ID and finish reason, usage,
input/cache-hit/cache-miss/output/reasoning/total token columns, configured rates, estimated cost,
streaming mode, status, timing, and errors. Partial streamed usage survives cancellation or failure.
`run_usage` stores cumulative run and agent totals after every call change, including completed,
failed, cancelled, pending, and missing-usage counts. Provider cache-hit and cache-miss call counts can
overlap: one call can contain both kinds of input. Unknown cache usage is reported separately.
Local checkpoint replays are saved in `checkpoint_hits` and never counted as billable AI calls.

`run_events` retains the full event history even when the live UI trims its event list.
`run_status_history` records lifecycle changes, and `run_snapshots` retains the patch diff, approval,
pull/merge request result, and session details. Archive reads work after an API restart through
`GET /api/runs?limit=100`, `GET /api/runs/{id}`, and the existing session/log/report endpoints.
Archived sessions are available for review; approval and cancellation still require a live session.
The tables are added automatically on startup without replacing existing data. Older runs without
saved token metadata retain their existing logs; their missing usage cannot be reconstructed.

## Safety model

- GitHub accepts HTTPS `github.com/{owner}/{repository}` URLs. GitLab accepts HTTPS URLs on the
  configured `GitLab:Host`, including nested groups. This deployment defaults to `gitlab.dotin.ir`;
  set `GitLab__Host` to override the self-managed GitLab hostname.
- Git credentials are passed to Git through process environment configuration, never clone URLs.
- Repository tools are limited to bounded text-source list, search, read, write, status, and diff.
- Absolute paths, traversal, `.git`, symbolic links, binary files, and oversized files are blocked.
- Agents cannot run repository code, shell commands, tests, package installers, or deployment tools.
- Git hooks and Git LFS smudge execution are disabled for clone, commit, and push operations.
- Changes remain in an isolated clone until approval.
- Approval creates a new branch and commit. Push is optional and never targets the base branch.
- Tokens are held only in the server's in-memory session. They are not returned by the API.
- Supporting documents are processed in memory, indexed into a per-run Qdrant collection, and the
  collection is deleted when the workflow ends (or after the configured short expiry). They are never
  copied into the isolated Git clone or included in its diff.

This is a local sample without user authentication. Do not expose it to an untrusted network.

## Run

```powershell
dotnet restore
dotnet test
dotnet run --project src/Maf.Handoff.Api --urls http://127.0.0.1:8080
```

Open `http://127.0.0.1:8080`.

Configure the protected graph database with `CodeGraph__DatabasePath`; it defaults to
`E:\CodeGraph\codegraph.db`. The database must contain an up-to-date index for the C# repository the
agents will inspect. `CodeGraph__RootPath` controls the local CodeGraph indexer used by the workbench.
Set `CodeGraph__Enabled=false` to disable CodeGraph for every agent workflow. When the server setting is
enabled (the default), each run can still opt out with the **Use CodeGraph for C# analysis** checkbox or
the `codeGraphEnabled: false` session API field.

The LLM checkpoint and tool-call database defaults to `data/llm-checkpoints.db` under the API content root. Override
it with `Checkpointing__DatabasePath`. Prompts, conversation history, tool results, and model responses
are stored in plain text, so keep this local database access-controlled and apply an appropriate
retention policy. If a future tool has non-idempotent external side effects, exclude those calls from
reuse before exposing that tool to checkpointed agents.

### Reference-document RAG

Start Qdrant locally before attaching reference files:

```powershell
docker compose -f docker-compose.qdrant.yml up -d
```

When selecting a Jira task, the first five supported attachments are checked by default. Uncheck individual files
or choose **Exclude all** before using the task. Changes to checkboxes on the selected task take effect
immediately. Selected files are automatically downloaded and their extracted text is sent to the LLM
before the workflow starts, even if the Jira agent is disabled. Excluded files cannot be read by the
workflow's Jira tools. Jira attachment loading supports text/source files, DOCX, and text-based PDF,
up to 5 MB per file and 5 selected files. Automatic context includes up to 12,000 characters per file
and 48,000 characters total; the Jira agent can page through the rest of selected files. Loading
failures and truncation are reported in the model context. Images and scanned PDFs need conversion
to text first. API callers can supply `jiraIssueKey` and `jiraAttachmentIds` on session creation;
an empty list excludes all attachments for that task.

Repository change branches use `user/WEPOD-12345`. The task number comes from the selected
WEPOD Jira key, or from a key such as `WEPOD-12345` or an explicit `task number 12345`
in the manual description. A missing or ambiguous task number returns a validation error
before cloning or running agents. Approval uses the generated branch; other branch names
are rejected.

The UI accepts files or a folder containing text, Markdown, CSV, JSON, XML, YAML, HTML, source files,
and DOCX. Local Ollama is the default embedding provider and needs no API key. Install it and pull the
configured model before attaching files:

```powershell
ollama pull embeddinggemma
```

OpenAI's `text-embedding-3-small` remains available as an optional embedding provider, using the
separate embedding key or the OpenAI provider key already entered for an agent. Retrieval is exposed
to every agent through a read-only `search_documents` tool. Its built-in router uses exact keyword
matching for quoted text, identifiers, paths, and code symbols; it uses Qdrant vector search for
natural-language questions. Each attachment set is limited to 50 files, 5 MB per file, and 25 MB total.
PDF/OCR ingestion is intentionally not included yet, so
convert scanned PDFs to text or DOCX before attaching them.

Enter the Qdrant Cloud cluster URL and database API key in the reference-document form; they stay only
in server memory for the temporary collection. The default Qdrant endpoint is `http://127.0.0.1:6333`
and the default Ollama endpoint is `http://127.0.0.1:11434`. Configure another endpoint, its API key,
or different embedding models with
`Rag__QdrantUrl`, `Rag__QdrantApiKey`, `Rag__OllamaEndpoint`, `Rag__OllamaEmbeddingModel`,
`Rag__OpenAiEmbeddingEndpoint`, and `Rag__OpenAiEmbeddingModel` environment variables. Do not expose
an unsecured Qdrant or Ollama endpoint on a network.

### Cognia organization knowledge

The connection step can also enable Cognia as a separate, read-only knowledge source. Enter the
Cognia base URL, client application ID, client secret, and one or more numeric knowledge-base IDs.
The server exchanges the client credentials for a short-lived machine bearer token and calls
`POST /api/engine/search`; it does not upload, update, or delete Cognia knowledge. Credentials remain
in server memory for the run and are not written into the workflow configuration or returned to the
browser. Prefer an HTTPS Cognia endpoint because client credentials are sent to its authentication
API.

When both Cognia and uploaded reference files are enabled, the agents' `search_documents` tool
queries Cognia and the temporary Qdrant collection in parallel. A failure in one source is reported
without discarding results returned by the other source.

The UI requests:

1. GitHub repository URL or GitLab project URL, plus the base branch.
2. The selected source controller's token for a private clone, optional push, and pull/merge-request creation.
3. AI provider, model, and reasoning effort for each agent, plus keys for selected providers.
4. A concrete code-change instruction with expected behavior and tests.

To push an approved branch, the selected token needs repository write access. On GitHub, the
Generate PR action also needs pull-request write access. On GitLab, Generate MR requires API access
and permission to create a merge request. The action pushes a locally committed agent branch when
necessary, then opens a request into the selected base branch. For a public repository with no push
or pull/merge request, the source-control token may be omitted. Provider API keys are always required
and are never reused as source-control credentials. Blank UI tokens fall back to `GitHub__Token` or
`GitLab__Token` in server configuration.

The session API accepts an `agents` object keyed by the stable names `jira`, `planner`, `developer`,
`reviewer`, `security`, and `release_manager`. Every entry contains an independent `provider`
definition and `apiKey`. Planner, developer, reviewer, and release manager are required; Jira and
security are optional. For example, the planner entry is:

```json
{
  "agents": {
    "planner": {
      "apiKey": "...",
      "provider": {
        "id": "gpt-5.6-sol",
        "name": "OpenAI",
        "url": "https://api.openai.com/v1",
        "toolCalling": true,
        "vision": true,
        "maxInputTokens": 1050000,
        "maxOutputTokens": 128000,
        "thinking": true,
        "reasoningEffort": "max",
        "supportsReasoningEffort": ["low", "high", "max"]
      }
    }
  }
}
```

A real request must also supply developer and release-manager entries.

## Projects

- `Maf.Handoff.Core`: agents, handoff workflow, OpenAI-compatible client, Git and safe tools
- `Maf.Handoff.Api`: session API and browser workbench
- `Maf.Handoff.Cli`: advisory CLI using `OPENAI_*` environment variables
- `Maf.Handoff.Tests`: configuration, topology, and repository-boundary tests

See [the architecture](docs/architecture.md).

See [agent conversation verification](docs/agent-conversation-verification.md) for the tested
handoff, remediation, failure, and termination scenarios.

## Provider definition API

```json
{
  "id": "deepseek-v4-flash",
  "name": "DeepSeek",
  "url": "https://api.deepseek.com/chat/completions",
  "toolCalling": true,
  "vision": true,
  "maxInputTokens": 128000,
  "maxOutputTokens": 160000,
  "thinking": true,
  "reasoningEffort": "high",
  "supportsReasoningEffort": ["low", "medium", "high", "max"],
  "inputCostPerMillionTokens": 0.25,
  "outputCostPerMillionTokens": 1.00
}
```

Pricing fields are optional and deliberately supplied by the caller so the cost estimate can use the
rates applicable to its account and date.

## References

- [Microsoft Agent Framework agents and tools](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/)
- [Microsoft Agent Framework handoff orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff)
- [GitHub Models inference API](https://docs.github.com/en/rest/models/inference)
