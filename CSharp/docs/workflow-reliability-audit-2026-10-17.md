# C# workflow reliability audit — 2026-10-17

The principal observed problem is agent livelock: agents continue spending requests without resolving contradictory evidence or completing their assigned role. The last three stored workflows do not prove a circular lock deadlock. The code does contain cancellation gaps that can leave work stuck, and persistence paths that can delay both execution and cancellation.

Scope: architectural review of the C# API, CLI/core workflow, repository tools, persistence, external-service/process boundaries, browser polling, and existing tests. This is a static review plus examination of local durable evidence, not a production load test or proof that every possible deadlock is excluded. Application code and historical run records were not changed.

## Last three workflows

Selected by `workflow_runs.created_at DESC` from `src/Maf.Handoff.Api/data/llm-checkpoints.db`, opened read-only. Times below are Tehran time on October 17. Stored provider-call counts exclude checkpoint hits; no checkpoint-hit records were found for these runs. Historical executions may precede some of the current source changes.

| Run | Started | Stored outcome | Provider calls | Recorded input tokens | Evidence |
|---|---|---|---:|---:|---|
| `b525f9d1-c892-4d45-9f0e-2632fd45823c` | 11:05:33 | Failed at 11:17:54; developer reached 30 model turns | 43 | 2,129,356 | Reviewer disputed whether the changes existed; developer supplied current diff/status; reviewer subsequently acknowledged changes but continued discussing inconsistent file listing and an identity finding. |
| `578520bf-0f17-4299-adcb-068c3930eaf8` | 10:59:56 | Still recorded as running | 19 | 628,375 | Last event at 11:01:45 requests `read_jira_attachment`, attachment ID `0`. The corresponding tool row remains running without a completion time. |
| `8a312eb8-f7fa-45c1-bb88-83bce62702dc` | 10:17:37 | Failed at 10:55:07; loop detected | 59 | 5,182,728 | Reviewer received recovery on visit 3, stopped on visit 4. Final response trace includes attempted `replace_repository_text` and `write_repository_file`, unavailable to reviewer. Largest recorded reviewer input: 135,002 tokens. |

The unfinished attachment record establishes an incomplete lifecycle, not its cause. A process interruption, older deployed code, or stalled work could explain it. Current selection code rejects excluded attachments immediately; do not attribute this historical record to a specific current attachment defect without deployment/process logs.

## Findings, in priority order

### 1. P1 — Repository tools disagree about new files

`RepositoryWorkspace.cs:165` lists only `git ls-files` (tracked files); `Search` at line 249 consumes that list. New files written by Developer remain untracked until approval. Diff and direct reads can see them, but listing/search cannot. The latest run's reviewer explicitly cited this inconsistency as a blocker.

Enumeration is also truncated before filtering: `RunGit` caps output, `ListFiles` caps the resulting list, and `Search` consumes that presentation text. Large repositories can yield false negatives or a truncation marker treated as a file path. `ContainsCSharpSource` inherits incomplete enumeration.

Fix: one internal, untruncated, NUL-delimited enumeration of tracked and untracked, nonignored files; filter allowed paths before presenting paginated results. Search must consume paths rather than formatted display output. Acceptance tests should create a new file, then prove list/search/read/diff agree, and find a target beyond current output limits.

### 2. P1 — Handoffs do not enforce fresh, useful evidence

`CompletedToolCycleFilteringChatClient.cs:53–87` drops completed ordinary tool calls/results once a later handoff exists. There is no mandatory structured replacement containing patch version, findings, current evidence, and the next required action. Existing assistant prose survives, including stale claims and other agents' reasoning. `HandoffWorkflowFactory.cs:69–94` supplies routing descriptions, not a validated evidence contract.

The latest run's reviewer made claims about repository inspection although its audited tool calls contain only a CodeGraph prefetch. It therefore did not freshly verify those claims through the audited repository tools. The earlier run demonstrates role confusion through unavailable editing-tool requests. These observations are consistent with unreliable history use; they do not prove one decorator is the sole cause.

Fix: persist a handoff record containing sender/recipient, patch fingerprint, goal, changed paths, evidence references, unresolved finding IDs, decision, and next action. Inject a concise current-role brief and current diff manifest at activation. A reviewer returning work must cite a current finding and evidence; contradictory evidence should trigger a fresh snapshot, not another unchanged handoff. Preserve detailed history for audit, outside the default model context.

### 3. P1 — Loop detection measures novel output rather than meaningful progress

`WorkflowLoopGuardChatClient.cs:23–68` treats every previously unseen serialized ordinary tool result as evidence. New timestamps, changing diagnostics, or varied errors can keep changing the state hash without improving the patch or resolving a finding. Conversely, it excludes tool arguments: identical results such as `No matches.` from different legitimate searches collapse to one piece of evidence. The shared accumulated evidence set also lets another agent's novel output affect subsequent state comparisons.

Recovery is a prompt plus one more attempt, tracked once per agent for the entire run. It neither validates the recovery action nor distinguishes independent later stalls. Hard request/turn limits still bound compliant model execution, so this is not a claim that model calls are literally unlimited. They are costly fallback stops, not successful recovery.

Fix: track progress as patch version, finding resolution, verified gate transitions, and newly relevant evidence. Separately track repeated normalized tool requests/results and invalid-tool attempts. Emit typed signals such as `NoProgress`, `RoleViolation`, `EvidenceConflict`, and `ToolFailure`. Permit one bounded recovery per stall episode: refresh evidence, restate the role and required decision, then reroute through a validated transition or terminate with a concrete blocker. Do not simply raise the turn limit.

### 4. P1 — Process waits can escape run cancellation

`RepositoryWorkspace.cs:422–434` computes a fingerprint while holding `_progressGate`, synchronously waiting on Git with `CancellationToken.None`. Other synchronous Git tools at line 594 also discard cancellation. `RunGitAsync` at line 618 waits without a local deadline and has no process-tree termination on cancellation. Disposing the process wrapper is not a child-process cleanup strategy.

`CodeGraphService.cs:189` waits for restore/index subprocesses without cancellation or timeout; a hung process keeps indexing occupied. `RepositoryBuild.cs:59` has an unbounded post-kill wait/output drain, despite the useful five-minute primary build limit.

Fix: cancellable async process execution with per-operation and overall deadlines, concurrent bounded output collection, process-tree termination, and bounded cleanup. Do not hold a monitor during Git or filesystem hashing. Test a deliberately hung subprocess and verify prompt cancellation plus no surviving child. This is a potential hang/starvation path; no circular monitor dependency was established.

### 5. P1 — Durable runs have no durable execution ownership

`RepositorySessionService.cs:18,85,124` keeps live state in memory, launches an untracked task, and falls back to stored snapshots after restart. There is no startup reconciliation or execution lease/heartbeat that changes abandoned `running` records to an interrupted state. The middle run is the concrete symptom, although its interruption cause is unknown.

Fix: a hosted worker with bounded admission, tracked tasks, shutdown cancellation, owner ID, heartbeat and lease expiry. Reconcile incomplete run/tool/provider rows on startup. Explicitly distinguish `running`, `awaiting_input`, `interrupted`, and `stalled`; never automatically replay uncertain external writes. Completed sessions, workspaces, cancellation sources and provider resources also need an explicit retention/disposal policy.

### 6. P1 — Terminal approval is parsed from prose, without all required gates

`HandoffTopology.cs:45–85` uses substring matching for control markers. Quoted/negated marker text in assistant output can be interpreted as control. Completion checks reviewer prose but never verifies `SECURITY_APPROVED` when Security is enabled. Review approval is invalidated by developer speech, not tied to the actual patch fingerprint. Routing descriptions ask Security to approve, but do not enforce its approval in code.

Fix: typed coordinator-owned decisions with gate identity and patch fingerprint. Completion must require current reviewer approval, current security approval when enabled, and the selected build policy. Ordinary assistant text must not issue supervisor stop signals. Test missing security approval, quoted markers, and approval for an earlier patch. This is a static correctness finding, not an observed successful bypass in these three failed runs.

### 7. P2 — Persistence amplifies work and can block session responsiveness

`RepositorySessionService.cs:318–320,402–406,658–662` persists each streamed event, updates status and serializes the whole snapshot. Event persistence runs under the session lock; SQLite contention can therefore block status reads and the initial cancellation state transition. The latest run recorded 2,013 events, each triggering this persistence path.

`AiCallStore.cs:171–184` rereads and deserializes all prior AI call records, including large input/response JSON, every time usage is recomputed. With N calls this repeatedly processes accumulated history, increasing work and writer-transaction duration. Across the earlier run, 5.18 million input tokens were recorded; persistence and model context both carry substantial repeated data. This review did not measure the share of latency caused by SQLite.

Fix: ordered bounded event batching, immediate durable terminal transitions, periodic compact snapshots, and incremental or scalar-SQL usage aggregation. Keep database I/O outside session locks, preserving event ordering. Benchmark concurrent sessions while measuring cancellation latency, write duration and queue depth.

### 8. P2 — Redundant builds and buffered streaming conceal activity

`RepositoryBuild.cs:23` builds every solution and every project, including projects already covered by a solution. Repeated forward-review attempts rebuild unchanged content. Prefer a solution coverage plan plus uncovered projects; reuse verification only for the same patch and environment identity.

Developer/reviewer guards and Jira reporting/clarification decorators buffer whole responses before yielding. Checkpoint and usage decorators also retain updates for recording. This increases memory use and can leave the UI without visible progress during long reasoning/tool/build work. Add explicit phase/heartbeat events and bounded buffers; release only validated tool calls while allowing safe progress text where possible.

Browser polling (`wwwroot/app.js:1320`) uses an interval without an in-flight guard or ordinary poll timeout. Slow status requests can overlap, and older responses can overwrite newer state within the same session generation. Schedule the next poll after completion and reject stale snapshot versions.

## Existing protections worth retaining

- `ExclusiveHandoffChatClient` prevents mixed outstanding tools and handoffs.
- Shared execution budgets cover model/tool rounds and checkpoint replays.
- Loop detection stops paraphrased unchanged handoffs and preserves the patch.
- Reviewer build guard distinguishes environment blockers from code remediation.
- Run cancellation is forwarded to framework execution, not just event observation.
- Clarification uses asynchronous continuations and cancellable waiting; examined semaphores release in `finally`/lease cleanup. Nested use of the same session monitor is reentrant, not by itself a deadlock.

## Recommended implementation sequence

1. Fix repository evidence consistency and add the new-file/large-repository regression cases.
2. Add typed, patch-versioned handoff and approval records; fresh reviewer evidence; signal-based bounded recovery.
3. Make subprocesses cancellation-safe and introduce durable run ownership/reconciliation.
4. Batch persistence, aggregate usage efficiently, bound context and remove redundant build work.

Recovery acceptance scenarios should cover unchanged reviewer/developer ping-pong, reviewer attempts to edit, varied errors with no progress, contradictory stale evidence, legitimate new searches returning the same text, a hung Git child, application restart mid-tool, database writer contention during cancellation, and completion with missing/stale approval. Deterministic scripted clients should verify transitions and budgets; sampled real-model runs should assess whether handoff content is useful.

## Validation

`dotnet test CSharp/Maf.HandoffWorkflow.slnx --no-restore --verbosity quiet`: **543 passed, 3 failed, 546 total**. Re-running the three failures reproduces them. `AutomaticCompletionTests.cs:107` reflects `Validate` using static binding flags, but `RepositorySessionService.cs:479` defines it as an instance method. The resulting null reflection lookup throws `NullReferenceException` before the intended assertion. These tests currently do not validate completion dependencies.

No production fixes were applied during this review. No agents or external writes were triggered. Read-only inspection helpers are under ignored `CSharp/artifacts/architecture-audit/`.
