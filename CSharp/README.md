# Repository Change Workbench

Optional Talk notifications for finished steps, human input requests and workflow
outcomes are available through the separate `Maf.Handoff.Talk` project. See
[Talk setup and delivery behavior](docs/talk-notifications.md). Notifications are disabled by default.

Project scope: this C# application is the sole active project for requests and prompts. The sibling Python project is out of scope unless the user explicitly requests work on it. See `../AGENTS.md` and `AGENTS.md` for the mandatory agent instructions.

A .NET 10 sample that connects Microsoft Agent Framework 1.19 handoff agents to a GitHub or GitLab
repository. The agents inspect and edit an isolated clone, then the browser shows the exact Git diff
as a browsable changed-file tree with per-file add/delete stats and a toggle between inline and
side-by-side layouts. Commit, push, and PR/MR creation require either explicit approval after review
or the corresponding completion selections authorized before starting the run.

In **Connection**, choose **Review GitLab MR and apply feedback** and select an open MR from the repository's
merge request table. Titles link to GitLab in a new tab. Refresh reloads the list; fork MRs are shown
but cannot be selected. The server reads the open MR and all discussion pages using the configured or supplied
GitLab token, then clones its source branch for a fresh agent run. Input includes the title, description,
general comments, unresolved threads, replies, authors, timestamps, and file/line positions. Resolved
threads can optionally be included as context. Review requires the Jira agent and starts by reading the
full linked Jira task. Select the task explicitly, or let the run identify one unambiguous key from the
MR title, description, source branch, and commit messages. Ambiguous or unavailable requirements must
be reported as an evidence gap. Selected comments and attachments remain respected.
Each run retains the fetched review context, commit list, pinned base/head SHAs, and changed-file scope
in its input event history. Agents review the complete change from GitLab's MR merge base to its source
head, including all MR commits and current fixes. Unrelated target-branch changes are excluded. A source
branch that moves after context loading requires a new review. Approval shows only fixes from this run;
an MR that needs no fixes can finish without manufacturing a new patch.

Choose one of three review modes:

- **Independent MR review** inspects and fixes the implementation against task requirements and acceptance
  criteria from the linked Jira task, MR description, additional instructions, and attachments. It does not fetch or use
  GitLab comments or discussion threads, even if resolved discussions were previously selected.
- **Apply reviewer feedback** evaluates GitLab comments and unresolved threads, applies relevant fixes,
  and verifies them without a separate requirements audit.
- **Independent review + reviewer feedback** (default) performs both and reports their outcomes separately.

All review modes use only **Jira → Reviewer → Release Manager**. Planner, Developer, and Security are
disabled. Reviewer applies and verifies applicable fixes; these fixes do not receive a separate Developer/Reviewer
pass. Switching back to a change request restores the previous agent selection. All modes retain the
configured build policy. Resolved threads are
optional context for the feedback and combined modes. Step 5 displays the chosen scope.
For independent and combined reviews, **Comment issues on the source code** optionally adds concise
comments prefixed with `Agent Review:` at confirmed finding locations in the checked-out source,
in addition to the final report. It is off by default. Comments use valid file syntax, avoid duplicates,
and describe fixes accurately when the issue is resolved during the run. Findings in formats without
comment support remain in the report. These are repository edits, visible in the diff and subject to
the normal review and commit/push selections; they are not posted as GitLab discussions.
Approved commits and authorized
pushes use the existing MR source branch; this mode does not create another MR, post replies, or resolve
threads. Only open MRs whose source and target are in the selected project are supported. Read access
is required for intake, and pushing requires repository write access. Review intake fails rather than
silently truncating discussions when it exceeds 100 pages or 200,000 characters of context.

In **Agents**, **Autonomy and completion** offers five autonomy levels: 0% Guided (ask before major
decisions), 25% Assisted (ask about unclear requirements), 50% Collaborative (resolve minor
ambiguity), 75% Highly autonomous (infer decisions and ask only when essential), and 100% Fully autonomous
(the default; make decisions, record assumptions, and continue without waiting for human input).
In browser sessions, all agents receive
the selected decision policy and an `ask_user_decision` tool, including runs with Jira disabled. At 100%,
both generic and Jira clarification paths return delegated-decision guidance immediately; that guidance
is never presented as a factual answer from the user. Intermediate levels guide the model's judgment
about when to ask; they are not probabilities or confidence scores.

Automatic **commit**, **push**, and **PR/MR creation** are separate upfront selections, all off by default.
Push includes commit; PR/MR creation includes both. Actions run only after successful workflow review.
At 100% without automatic commit, a successful run ends as `completed` with its patch available for
inspection and optional later approval. Other levels retain the normal final approval step unless
automatic commit was selected. Autonomy does not disable independent review, build policy, execution
limits, budgets, or cancellation. Missing prerequisites and unresolved defects remain visible failures.

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
DeepSeek's missing `reasoning_content` error, including final-response requests that remove available tools
but retain earlier tool calls. Requests with complete history keep the configured reasoning.
OpenAI reasoning values are passed through its Chat Completions options. Tool calling is mandatory
for this handoff topology.

Model clients use a 120-second HTTP and SDK network timeout.
AI provider HTTP requests use [Polly](https://www.pollydocs.org/strategies/retry) for three retries
after the initial attempt, waiting 1, 2, and 4 seconds. Network failures and HTTP 408, 429, and 5xx
responses are retried; other errors and cancellation propagate immediately. SDK retries are disabled
to avoid stacking policies. Retries happen before a response is returned, so interrupted response
bodies or partially delivered streams are not replayed. These transport attempts belong to one logical
AI call in the existing request-budget and audit counters; checkpoint replays make no HTTP requests.

In **Agents**, enable **Retry on workflow agent** and set **Number of retries** (1–10, default 2)
to restart a failed workflow automatically. This option is off by default. Each retry waits two seconds,
clones the base branch again, starts a fresh agent conversation, and includes the previous error as
diagnostic context. Partial patches are not carried forward. Retry events and all attempts remain in
the same session report, and AI calls and estimated cost share the original run limits. Cancellation,
input requests, release rejection, unavailable build prerequisites, and configured workflow limits stop
the run without retries. Commit, push, and PR/MR failures do not restart the workflow.
The API accepts `retryOnWorkflowAgent` and `numberOfRetries` on session creation.

For repositories containing tracked C# source, planner, developer, reviewer, and security agents can
also receive the read-only `find_symbol`, `get_context`, `get_context_by_id`, and `get_neighbors` tools.
CodeGraph is complementary: agents use a focused query when it can make analysis faster or cheaper,
then continue with targeted repository evidence when the graph has no useful match. Missing, stale, or
unavailable graph data never blocks a handoff or the workflow. Configuration, documentation, and other
non-C# work normally needs no graph call.

Use **Coding Rules** in the top bar to add, read, edit, or delete shared coding standards,
syntax preferences, and team style conventions. In **Advanced Run → Agents → Coding Rules**,
select **Include Coding Rules in this agent run** to apply enabled rules to planning,
implementation, and review. This option is checked by default in Advanced Run and disabled in Simple Run.
Use the **Enabled** table column to include or exclude individual rules; new and existing rules
start enabled. If none are enabled, the run receives no coding-rule context. Each run saves a snapshot, so later edits, enablement changes, and deletions
do not affect active runs, retries, or revisions. Rules persist on the server in
`data/coding-rules.db`; configure `CodingRules:DatabasePath` to use a different location.

The setup starts in **Simple Run**. Choose **Simple Run** or **Advanced Run** at the beginning of the
Connection step; the selection applies to the whole run and is hidden on later steps. Return to
Connection to change it. **Advanced Run** exposes all
settings. Simple Run offers AIFA and DeepSeek credentials and independent MR review only. It uses
fully autonomous execution, Jira requirements validation, and build verification, with Release
Manager validation, workflow retry, source comments, resolved discussion context, and automatic
commit/push/PR creation off. CodeGraph defaults to enabled in both modes; its checkbox is available under Advanced Run → Connection → Code analysis.
Task complexity stays Normal; per-agent turn limits use their defaults
and additional guidance is empty. Pricing, Jira connection tests, additional Jira actions, raw Jira
responses, Jira comments/attachment controls, and advanced agent settings are hidden. Step 5 follows
the same visibility rules. Switching back to Simple Run reapplies these defaults. Jira comments and
attachments retain their existing context-selection behavior behind the hidden controls.

In Advanced Run, each agent has an independent OpenAI, DeepSeek, Claude, or AIFA provider selector, model selector, and
reasoning-effort selector. All agents default to DeepSeek `deepseek-flash` with low reasoning,
but can be configured individually. Provider credentials are entered once and reused only by agents
assigned to that provider. Endpoints and capability metadata remain internal.

Before starting a run, **Review & confirm** shows the full change request, repository and service
settings, enabled and disabled agents, model selections, reasoning, guidance, token pricing, and
selected reference and Jira attachments. Credentials appear only as configured or missing.
When a run ends, **Agent summaries** shows a separate short note written by each enabled agent
about its own work, outcome, and blockers. Every agent is strictly instructed to write these summaries
in Persian (Farsi), regardless of the request language, preserving technical identifiers as needed.
Remediation replaces that agent's earlier note.
Agents that were not reached or did not provide a summary are identified explicitly. Summaries
remain available in saved runs and require no additional model requests. The CLI prints them
at the end of the run as well.
Use **Edit** to return to a setup step; returning to review rebuilds the summary from the current
values. Documents are indexed and the session starts only after **Confirm and start agent run**.

If you need to correct a setting during execution, choose **Stop run**, then **Edit configuration**.
Your form values are retained. Review the corrected settings and confirm to start a new run with
a fresh isolated clone and conversation; the stopped run remains in history and is not committed or pushed.
After a run stops or finishes, **Restart Agent Run** returns to Connection with the existing form inputs
and selected attachments retained. Edit any setup step, review the inputs, and confirm to start a new run.

To revise a finished, uncommitted result, choose **Request changes**, enter a **Modification prompt**,
and select **Revise result**. Agents work on the same isolated clone, keeping the original request,
all prior modification prompts, and the existing patch. Each revision runs the normal review gates
with the configured execution limits and AI budget applied again. You can request more changes or
approve the updated diff; revisions never automatically commit, push, or create a PR/MR.
This also works when the workflow finishes without changes. Modification prompts remain in the
run report, and usage totals include all revisions. Revisions require a live session; archived runs
after a server restart remain read-only. Attached references stay available until their configured
collection lifetime expires; after expiry, start a new run with the references attached again.

Jira and security are optional and start disabled. Every run uses Planner to produce a
repository-backed implementation contract before Developer changes the repository. In the Agents step,
**Validate requirements in the Jira agent** is checked by default. Uncheck it to make Jira gather only
issue details, included comments, and selected attachments, then hand the unvalidated input to Planner.
In this mode Jira has no repository inspection, clarification, or problem-reporting tools and no automatic
problem reporting. Planner validates requirements before planning; later agents retain their own checks.
The choice appears in run confirmation and saved configuration. API clients can set
`jiraValidationEnabled: false`; omitted values retain validation. The choice applies when Jira is enabled.
With Jira validation enabled,
it assesses task readiness before handing evidence to Planner: full issue details, discussion and decisions,
acceptance criteria, selected attachments, a targeted code/test/documentation inspection, dependencies, and
integration risks. Its read-only repository and document tools support that assessment. It reports READY,
READY_WITH_ASSUMPTIONS, or NEEDS_CLARIFICATION with source citations. Blocking ambiguities produce focused
questions according to the autonomy policy. Below 100%, questions can pause the active session at
`awaiting_input` before planning or implementation. At 100%, agents receive delegated-decision guidance
and continue using documented assumptions. For interactive questions, answer in
the agent-run screen to resume the same Jira agent with its context and repository clone. The execution
timeout is 50 minutes and pauses while waiting; cancellation remains available. Answering a clarification
restarts the 50-minute timeout. Follow-up questions use the same run.
Non-blocking gaps are handled according to the selected autonomy policy.
Planner, Developer, Reviewer, and Release Manager are required. Release Manager validation is off by default: it finalizes from prior approvals with a brief summary and no inspection tools. Enable **Release Manager validation** in the UI (API: `releaseManagerValidationEnabled: true`) to restore final diff and evidence checks. Both modes require Reviewer approval and, when assigned, Security approval in the Release Manager instructions; the existing workflow approval checks remain unchanged.

The Jira task preview has an **Include Jira comments in the AI context** checkbox, enabled by default.
Uncheck it to exclude comments from the run; the backend filters comments from issue details and blocks
comment reads, while the preview still shows them. This controls reading comments, not posting reports.

When Jira finds unclear, incorrect, conflicting, incomplete, or otherwise problematic requirements,
it consolidates its evidence, impact, and clarification questions into one Persian comment on the related
task using `report_jira_problem`. The selected task takes precedence; a manual request must identify exactly
one full issue key. The UI immediately shows a Persian notice with the findings, posting status, and task link,
including on saved runs. Blocking findings wait for an answer before implementation; confirmed non-blocking
assumptions remain explicit. No comment is posted for a clean readiness assessment. Failed or uncertain writes are
not retried automatically, and a missing task or posting failure is clearly reported in the UI.
In the Agents step, **Build project** is checked by default. After agents finish applying and reviewing
changes, the workflow builds the solution and displays its result. A failure appears in bold red text
with build diagnostics, without starting another repair cycle. Uncheck it to skip the build; a persistent
notification records that compilation was not verified. The choice appears in run confirmation and
saved configuration. API clients can set `skipBuildVerification: true` to skip the build.

Workflow comments focus on critical points, with at most three short bullets. The final posted text is capped at 1,000 characters, including signature and headings; excess section content is truncated, preserving both sections. Every workflow comment passes through `JiraCommentHandler`, which adds `[MAF-AI:v1]` and two sections:
**محصول: مسائل و نیازمندی‌ها** for Product issues, requirements, acceptance criteria, and decisions;
**ملاحظات فنی** for technical considerations and validation blockers. Reporting instructions limit the
combined content to three short bullets and 800 characters, keeping detailed evidence in the run log.
Comment reads label signed reports as `mafOrigin: agent`, older MAF reports as `legacy_agent`, and other
comments as `external`. These content labels do not verify author identity or establish Product approval.
The agent attributes stakeholder decisions to their actual sources and avoids repeating unchanged agent findings.
Comment exclusion still applies to these reads.
Developer records material changes beyond the requested Product behavior through `report_jira_scope_change`.
Focused regression tests and necessary private helpers are in scope and do not require a scope-change comment.
A separate `jira_scope_change` event records the posting
outcome. Duplicate identical reports reuse their result, including uncertain writes. This capability is
available only when the Jira agent is enabled. Disabled Jira runs cannot access Jira or post comments.
Interactive clarification requires the active API process;
integrations without it retain the `JIRA_INPUT_REQUIRED` fallback. Questions and answers are persisted in
the run's event history.
Stopped runs show the complete agent explanation in a formatted panel with source attribution, highlighted
blockers and questions, an original-text view, and copy/edit controls. Clarification stops use an amber
"Clarification needed" label. The panel also reconstructs explanations for previously saved runs.
Reviewer independently inspects and confirms every change before release. Completion requires a
reviewer-issued `REVIEW_APPROVED` after the latest Developer work; missing approval stops the run with
`review_approval_missing`. Unless **Skip build verification and continue** is selected,
with a repository attached the reviewer's forward handoff automatically
runs `dotnet build` on its .NET solutions and projects before Security or Release Manager receives
the change. Compiler errors return diagnostics to Developer for fixes, followed by another independent
review and build. Known package-feed, missing-dependency, SDK, and build-environment failures stop with
an actionable build-blocked message. The skip option bypasses the build and preserves a notification
without claiming compilation passed. Existing remediation and workflow limits bound
these attempts. Each build has a five-minute timeout and bounded diagnostic output; cancellation
stops its process tree. Repositories without .NET build targets record an explicit skip. The host
must have a suitable .NET SDK and access to required package feeds; the Docker image includes the SDK.
Enable Security when the work's
context or risk needs it. In Agent settings, each agent has an editable Maximum model turns value:
120 for Developer and 60 for the other agents. A turn includes tool rounds and later handoffs back
to that agent. API callers can set `agents.<name>.maxAgentTurns` to a positive integer.
When omitted, the server uses `Workflow:MaxAgentTurns` (or `Workflow__MaxAgentTurns`, default 60),
with a minimum default of 120 for Developer. Explicit agent settings override that default.
The run's Maximum AI calls (default 200 across all agents) and USD budget still apply.
The saved run configuration records each agent's effective turn limit.
The Agents step also includes a Task complexity slider: Simple uses 0.5× limits, Normal uses 1×
(the default), and Hard uses 3×. It updates total AI calls, the USD budget, and all agent turn limits;
individual limits remain editable. The server scales the conversation message limit by the same
factor. API requests can set `taskComplexity` to `simple`, `normal`, or `hard`; explicit
`maxAiCalls`, `maxBudgetUsd`, and agent turn values are effective limits and are not scaled again.
Omitted agent turn limits use the server defaults scaled by complexity.
The API also limits total conversation messages, including tool calls and results, using
`Workflow:MaxTotalMessages` (or `Workflow__MaxTotalMessages`, default 2000). This is separate
from model turns and AI calls: one model request can add multiple tool messages. The effective
message limit is saved with each run. Reaching it preserves the patch without approving release.
For CLI and direct factory use, a run allows at most 25 model
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
current cached-input, uncached-input, and output price per million tokens in step 1 under
**LLM settings > Model token prices**, alongside provider API keys. Rates are shared by provider and model
across all agents and reasoning efforts. DeepSeek rates are refreshed from the provider's pricing page at page load and when
continuing from Request & references to Review & confirm for the current UTC price band; custom rates are preserved.
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

Repository runs with a selected Jira issue also index completed Jira intake handoffs in
`jira_intake_checkpoints`. SHA-256 fingerprints cover the title, description, all included comment pages
(including authors and timestamps), and selected attachments' full source bytes. The cache also matches
the issue's remaining fields, task input, Jira server/user, repository, reference collection, selection,
model, instructions, tools, and model options. Before reusing the intake assessment, its recorded
read-only repository, document and Jira tool results are re-read and compared. A hit restores the
assessment and evidence for Planner, skips the Jira model rounds, and counts as one local checkpoint
hit with no new provider tokens. Clarification/decision calls, problem reports, unknown tools, incomplete
snapshots and failed or partial responses cannot become reusable intake checkpoints. Jira reads still
run to verify freshness; excluded comments and attachments are not read for this check.

Each checkpoint row includes a `workflow_id`, allowing all provider-call attempts from the same workflow run
to be queried together. Actual local tool executions are stored separately in `tool_calls`, including the
caller agent, tool name, input JSON, response or error JSON, status, duration, timestamps, and the same
`workflow_id`. Framework handoffs are not tool executions and are therefore not inserted into this table.

Read-only tool results are cached in memory within each team/run and shared across its agents.
The cache keys include the tool, canonical JSON arguments, output representation, and source version;
Planner/Jira compact file reads never substitute for Developer/Reviewer full reads. Concurrent identical
successful reads share one execution. File reads verify a content hash; repository searches, lists,
status and diffs verify the Git file inventory, file metadata, Git state and agent-write revision.
Jira reads expire after 30 seconds and are invalidated by Jira reporting tools and user-decision tools.
CodeGraph reads verify database/WAL file metadata; custom providers returning no cache version bypass caching.
Writes, decisions, unknown tools, exceptions, cancellation and error responses are not retained.
The cache holds at most 128 completed entries and 8 MiB of serialized result payloads; oversized results
are not retained. Repository/graph entries also expire after one hour. Cache state is never reused by another run.
Cache hits are recorded separately in `tool_cache_hits` and exposed as `toolCacheHits` in run reports,
including the consuming agent, tool, input, cache key and timestamp. The report distinguishes these
reuses from actual executions in `tool_calls`.

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

For the full local quality check (Release build, C# tests with coverage, UI scripts, and CLI smoke tests), run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-quality-checks.ps1
```

Reports are written to `artifacts/quality-checks/`. The command returns a failing exit code when any
check fails, including the regression tests documenting open defects. See the
[test assessment and scenario matrix](docs/testing-assessment-2026-10-17.md) for results, reproducible
findings, and the remaining live-service and browser validation work.

Configure the protected graph database with `CodeGraph__DatabasePath`; it defaults to
`E:\CodeGraph\codegraph.db`. The database must contain an up-to-date index for the C# repository the
agents will inspect. `CodeGraph__RootPath` controls the local CodeGraph indexer used by the workbench.
Set `CodeGraph__Enabled=false` to disable CodeGraph for every agent workflow. When the server setting is
enabled (the default), CodeGraph is checked by default in both run modes. Advanced Run offers the
**Use CodeGraph for C# analysis** checkbox to opt out. Simple Run keeps it enabled unless disabled by
the server. API callers can opt out with the `codeGraphEnabled: false` session field.

For supported agents, the workflow prefetches one focused graph context when C# involvement becomes
visible and retains it on later model requests. Graph tools remain available for symbol resolution
and relationship queries, including when the initial prefetch finds no symbol. An empty lookup does
not disable the index; agents can use a better symbol identified from repository evidence.

Before agents start, the API compares the graph's saved SHA-256 source fingerprint with the actual
cloned checkout. The fingerprint includes relative file paths, C# source, project/solution files,
and build/dependency configuration under the repository root (or the source directory without Git).
Build outputs and repository metadata are excluded. A mismatch, missing source, or an unknown baseline
produces a CodeGraph warning in run activity and disables graph tools for that run; repository
inspection continues. This is notification mode: task startup does not automatically reindex.

Use the CodeGraph workbench to reindex a stale or unverified codebase. A changed or unknown fingerprint
forces a full refresh, even when incremental indexing is selected, because changes can affect
relationships in otherwise unchanged files. The baseline is saved in `maf_source_fingerprints` in the
graph database only after indexing finishes without restore/workspace failures and the source has
remained unchanged. NuGet package security advisories remain visible as warnings but do not prevent
verification when projects load successfully. Existing databases need one workbench reindex to establish that baseline. Direct
indexer runs invalidate verification when their `indexed_at` marker changes. The database overview
shows each codebase's freshness against its original indexed folder, with hashes under **Verification details**.
A local folder can be up to date while a run's checkout differs because it uses a newer revision or
another branch. Run messages identify both paths; update the indexed folder to the run's branch/revision
and reindex before starting a new run. The same local-folder check is available at
`GET /api/codegraph/freshness?dbPath=...&sourcePath=...`.

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
before the workflow starts when the Jira agent is enabled. Disabled Jira runs do not load attachments. Excluded files cannot be read by the
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

AIFA calls are free: every agent using AIFA reports $0 cost, including calls without token usage.
Input, cached input, and output rates are fixed at zero; other providers keep their configured pricing.

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
