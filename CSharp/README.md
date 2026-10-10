# Repository Change Workbench

Project scope: this C# application is the sole active project for requests and prompts. The sibling Python project is out of scope unless the user explicitly requests work on it. See `../AGENTS.md` and `AGENTS.md` for the mandatory agent instructions.

A .NET 10 sample that connects Microsoft Agent Framework 1.19 handoff agents to a GitHub or GitLab
repository. The agents inspect and edit an isolated clone, then the browser shows the exact Git diff
and requires user confirmation before any commit or push.

No Azure services or Azure packages are used. The workbench supports OpenAI through Chat
Completions, AIFA through the same OpenAI-compatible transport with organizational identity headers,
DeepSeek through its provider-native Microsoft Agent Framework adapter, and Claude through
Anthropic's official C# `IChatClient` integration.

Provider definitions include model identity, URL, tool-calling/vision/thinking capabilities, token
limits, and supported reasoning-effort values. Full `/chat/completions` URLs are normalized to the
SDK base endpoint. DeepSeek agents preserve the selected reasoning level without returning its full trace.
Reasoning metadata travels with assistant content across handoffs and is restored before subsequent tool
requests. If history from another provider, a workflow supervisor, or an older checkpoint lacks recoverable
reasoning, that request uses non-thinking mode while retaining its tool calls and results. This prevents
DeepSeek's missing `reasoning_content` error; requests with complete history keep the configured reasoning.
OpenAI reasoning values are passed through its Chat Completions options. Tool calling is mandatory
for this handoff topology.

AI provider HTTP requests use [Polly](https://www.pollydocs.org/strategies/retry) for three retries
after the initial attempt, waiting 1, 2, and 4 seconds. Network failures and HTTP 408, 429, and 5xx
responses are retried; other errors and cancellation propagate immediately. SDK retries are disabled
to avoid stacking policies. Retries happen before a response is returned, so interrupted response
bodies or partially delivered streams are not replayed. These transport attempts belong to one logical
AI call in the existing request-budget and audit counters; checkpoint replays make no HTTP requests.

For repositories containing tracked C# source, planner, developer, reviewer, and security agents can
also receive the read-only `find_symbol`, `get_context`, `get_context_by_id`, and `get_neighbors` tools.
CodeGraph is complementary: agents use a focused query when it can make analysis faster or cheaper,
then continue with targeted repository evidence when the graph has no useful match. Missing, stale, or
unavailable graph data never blocks a handoff or the workflow. Configuration, documentation, and other
non-C# work normally needs no graph call.

Each agent has an independent OpenAI, DeepSeek, Claude, or AIFA provider selector, model selector, and
reasoning-effort selector. All agents default to DeepSeek `deepseek-flash` with low reasoning,
but can be configured individually. Provider credentials are entered once and reused only by agents
assigned to that provider. Endpoints and capability metadata remain internal.

Before starting a run, **Review & confirm** shows the full change request, repository and service
settings, enabled and disabled agents, model selections, reasoning, guidance, token pricing, and
selected reference and Jira attachments. Credentials appear only as configured or missing.
When a run ends, **Agent summaries** shows a separate short note written by each enabled agent
about its own work, outcome, and blockers. Remediation replaces that agent's earlier note.
Agents that were not reached or did not provide a summary are identified explicitly. Summaries
remain available in saved runs and require no additional model requests. The CLI prints them
at the end of the run as well.
Use **Edit** to return to a setup step; returning to review rebuilds the summary from the current
values. Documents are indexed and the session starts only after **Confirm and start agent run**.

Jira and security are optional and start disabled. Every run uses Planner to produce a
repository-backed implementation contract before Developer changes the repository. When Jira is enabled,
it assesses task readiness before handing evidence to Planner: full issue details, discussion and decisions,
acceptance criteria, selected attachments, a targeted code/test/documentation inspection, dependencies, and
integration risks. Its read-only repository and document tools support that assessment. It reports READY,
READY_WITH_ASSUMPTIONS, or NEEDS_CLARIFICATION with source citations. Blocking ambiguities produce focused
questions and pause the active session at `awaiting_input` before planning or implementation. Answer in
the agent-run screen to resume the same Jira agent with its context and repository clone. The execution
timeout pauses while waiting; cancellation remains available. Follow-up questions use the same run.
Non-blocking gaps also give the user an opportunity to clarify before continuing.
Planner, Developer, Reviewer, and Release Manager are required.
When Jira finds unclear, incorrect, conflicting, incomplete, or otherwise problematic requirements,
it consolidates its evidence, impact, and clarification questions into one Persian comment on the related
task using `report_jira_problem`. The selected task takes precedence; a manual request must identify exactly
one full issue key. The UI immediately shows a Persian notice with the findings, posting status, and task link,
including on saved runs. Blocking findings wait for an answer before implementation; confirmed non-blocking
assumptions remain explicit. No comment is posted for a clean readiness assessment. Failed or uncertain writes are
not retried automatically, and a missing task or posting failure is clearly reported in the UI.
Developer records actual modifications beyond the Jira task's explicit requirements (including necessary
additional fixes and review remediation) through `report_jira_scope_change`. The Persian comment includes
affected files, behavior, reason, and validation. A separate `jira_scope_change` event records the posting
outcome. Duplicate identical reports reuse their result, including uncertain writes. This capability is
available even when Jira intake is disabled. Interactive clarification requires the active API process;
integrations without it retain the `JIRA_INPUT_REQUIRED` fallback. Questions and answers are persisted in
the run's event history.
Stopped runs show the complete agent explanation in a formatted panel with source attribution, highlighted
blockers and questions, an original-text view, and copy/edit controls. Clarification stops use an amber
"Clarification needed" label. The panel also reconstructs explanations for previously saved runs.
Reviewer independently inspects and confirms every change before release. Completion requires a
reviewer-issued `REVIEW_APPROVED` after the latest Developer work; missing approval stops the run with
`review_approval_missing`. With a repository attached, the reviewer's forward handoff automatically
runs `dotnet build` on its .NET solutions and projects before Security or Release Manager receives
the change. Compiler errors and other build failures return diagnostics to Developer for fixes,
followed by another independent review and build. Existing remediation and workflow limits bound
these attempts. Each build has a five-minute timeout and bounded diagnostic output; cancellation
stops its process tree. Repositories without .NET build targets record an explicit skip. The host
must have a suitable .NET SDK and access to required package feeds; the Docker image includes the SDK.
Enable Security when the work's
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
All agents also share a progress guard. The third visit by the same agent to the same repository content
and tool evidence triggers one role recovery attempt for that agent. Repeating the state after recovery
stops further model calls with `loop_detected`; the activity view shows the recent handoff path and preserves
the patch for inspection. Reworded assistant messages, changing tool-call IDs, and handoff payloads do not
count as progress. New code or new tool-result evidence does. Repository fingerprints hash complete
changed-file content, including new source files, even when the displayed diff is truncated. These snapshots
are cached between repository-tool writes in the isolated clone. Each model request also refreshes
the active agent's identity so shared history does not replace its role.
Configure the repetition threshold (minimum two, default three) with `Workflow__MaxRepeatedStates` for
the web app or `MAF_MAX_REPEATED_WORKFLOW_STATES` for the CLI. Request, turn, remediation, and cost limits
remain in effect, including for loops that continually produce different evidence. No loop recovery can
approve a patch or bypass a review gate.
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
The final usage cards show total and per-agent local checkpoint replays, including agents with zero new AI requests. Replays add no provider tokens or model cost; provider-cached input tokens remain part of provider usage and use the configured cache rate.

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
- Supporting documents are processed in memory for plain text search, with optional indexing into a
  per-run Qdrant collection for semantic search. References are deleted when the workflow ends
  (or after the configured short expiry). They are never
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
retention policy. Jira problem-comment requests and problem assessments are retained for audit but
excluded from checkpoint replay. If a future tool has non-idempotent external side effects, exclude those
calls from reuse before exposing that tool to checkpointed agents.

### Reference-document RAG

The Jira agent can read full task details and page through comments using the server's Jira configuration.
Only Jira receives the task-scoped problem-comment tool. It cannot choose another issue to comment on or
perform other Jira writes. Problem assessments are also reported automatically if the tool was omitted.
For acceptance criteria stored in a custom field, configure `Jira__AcceptanceCriteriaField` with its ID
(for example `customfield_12345`); the details response includes that field and its display name. Otherwise
the agent checks criteria in the description, discussion, and selected documents and reports missing criteria.
Comment paging follows [Atlassian's read-only issue comments API](https://developer.atlassian.com/server/jira/platform/rest/v10005/api-group-issue/).

Reference attachments default to **Plain text search (no external services)**. This mode needs no
Qdrant, Ollama, or embedding API key. All agents can search the extracted text using concise keywords,
quoted phrases, identifiers, and file names through `search_documents`.

To enable semantic search, select **Semantic and plain text search (Qdrant + embeddings)** under
**Document search** in connection settings. Start Qdrant locally or configure Qdrant Cloud:

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
and DOCX. For optional semantic search, local Ollama is the default embedding provider and needs no
API key. Install it and pull the configured model before attaching files in semantic mode:

```powershell
ollama pull embeddinggemma
```

OpenAI's `text-embedding-3-small` remains available as an optional embedding provider, using the
separate embedding key or the OpenAI provider key already entered for an agent. Retrieval is exposed
to every agent through a read-only `search_documents` tool. Its built-in router uses exact keyword
matching for quoted text, identifiers, paths, and code symbols. In semantic mode, it uses Qdrant vector
search for natural-language questions; in plain text mode, every query searches the in-memory text.
API uploads to `/api/reference-collections` accept `searchMode=exact` (default) or `searchMode=semantic`
as multipart form fields. Each attachment set is limited to 50 files, 5 MB per file, and 25 MB total.
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
queries Cognia and the attached documents in parallel. A failure in one source is reported
without discarding results returned by the other source.

The UI requests:

1. GitHub repository URL or GitLab project URL, plus the base branch.
2. The selected source controller's token for a private clone, optional push, and pull/merge-request creation.
3. AI provider, model, and reasoning effort for each agent, plus keys for selected providers.
4. A concrete code-change instruction with expected behavior and tests.

Selecting a repository loads its branches into a picker. Refresh branches to reload the list
after updating credentials or remote branches; manual branch entry remains available.
Base branch input is case-insensitive: `develop` resolves to `Develop` when that is the remote
branch's spelling. The resolved name is used for cloning and pull/merge requests. Exact matches
take priority; if multiple branches differ only by case, enter the exact name to disambiguate.

To push an approved branch, the selected token needs repository write access. On GitHub, the
Generate PR action also needs pull-request write access. On GitLab, Generate MR requires API access
and permission to create a merge request. The action pushes a locally committed agent branch when
necessary, then opens a request into the selected base branch. For a public repository with no push
or pull/merge request, the source-control token may be omitted. Provider API keys are always required
and are never reused as source-control credentials. Blank UI tokens fall back to `GitHub__Token` or
`GitLab__Token` in server configuration.

For repeated local tests, save the GitLab token once with
`dotnet user-secrets set "GitLab:Token" "YOUR_TOKEN" --project CSharp/src/Maf.Handoff.Api`
from the repository root, then restart the app in Development mode. When configured, **Use saved
GitLab token** is selected automatically. Clear that checkbox to enter a different token. Branch
loading and workflow startup use the same credential choice, and the form checks repository access
before starting a workflow or uploading reference documents.

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

## AIFA organizational service

Select **AIFA** for an agent to use `developer-model` at
`https://aifa-chatbot.dev.dotin.ir/v1/chat/completions`. Enter the bearer token in **AIFA API key**
without the `Bearer` prefix, or save it on the server:

```powershell
dotnet user-secrets set "Providers:AIFA:ApiKey" "YOUR_TOKEN" --project CSharp/src/Maf.Handoff.Api
```

Restart the API in Development mode after changing user secrets. In other environments, set
`Providers__AIFA__ApiKey`. The browser detects a configured token and allows the key field to remain
empty. The token is never included in the configured-provider response or saved workflow configuration.

Every AIFA call includes `x-user-id`, `x-session-id`, and `x-request-id`. Set `Providers:AIFA:UserId`
to the identity assigned by the service; the supplied curl's `123` is the initial configuration value.
By default, all agents share the workflow's session ID and each model call gets a new request ID
that stays unchanged during retries. To reproduce the supplied curl exactly, set
`Providers:AIFA:SessionId` and `Providers:AIFA:RequestId` to `beny`. These settings also support
environment variables with double underscores, such as `Providers__AIFA__UserId`.

For the CLI, set `MAF_MODEL_PROVIDER=aifa` and `AIFA_API_KEY`. Optional variables are `AIFA_MODEL`
(default `developer-model`), `AIFA_USER_ID` (default `123` from the curl), `AIFA_SESSION_ID`, and
`AIFA_REQUEST_ID`. Session and request IDs are generated when omitted.

The curl establishes text chat compatibility only. AIFA must also support OpenAI-style tool calls
for agent handoffs; confirm this with the service owner before running a workflow. Vision is disabled.
All AIFA requests, including streaming and CLI requests, send `"enable_thinking": true` and
default to `"reasoning_effort": "low"`. Each agent can select `low`, `medium`, `high`, `xhigh`, or
`max` in the reasoning effort selector. Live tests on October 10, 2026 returned HTTP 200 for each;
`min` and `minimal` returned HTTP 422 with the accepted names in the validation error.
The service also accepted integer effort `32`, but returned 282 reasoning tokens, so this is not
a verified token cap and numeric budgets are not offered in the app. Named levels also did not
produce consistently increasing reasoning counts in the sample test. No public AIFA documentation
was found, and `/openapi.json` and `/v1/openapi.json` returned HTTP 404.
The UI's 8,192 input / 2,048 output token metadata is provisional,
not a verified service limit, and is not sent as a token limit in requests. Pricing remains optional.

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
