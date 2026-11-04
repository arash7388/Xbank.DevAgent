# C# functional test assessment — October 17, 2026

The expanded suite finds release-gate and repository-evidence defects. This is a deterministic local
functional assessment, not a claim that every possible scenario is covered or that MAF is ready for release.
Failing regression tests assert the intended behavior and remain enabled; they are not skipped or changed
to accept the defects.

## Scope and method

- Ran the existing C# suite with Coverlet coverage and all standalone browser-code test scripts.
- Added 100 C# cases: 67 HTTP contracts, 21 repository scenarios, 10 decision-boundary scenarios,
  and two full scripted workflow cases for the optional Security gate.
- Added a UI payload matrix for GitHub/GitLab and LF/CRLF checkouts, plus six CLI process scenarios.
- Used actual ASP.NET routing, JSON binding, middleware, dependency injection, and temporary SQLite
  storage for API tests. HTTP dependencies are blocked and configuration is replaced with test values.
- Used temporary real Git repositories for repository tests and the Microsoft Agent Framework execution
  engine with scripted clients for workflow tests. No paid model calls or remote publishing were requested.
- Preserved pre-existing working-tree changes. Application source was not changed during this assessment.

Environment: Windows, .NET SDK 10.0.302, Node 14.2.0. Node runs each existing UI script directly because
this installation does not support `node --test`. Release builds avoid DLLs locked by the active Visual
Studio Debug session.

## Harness repairs

The baseline had 552 passing and three failing C# cases, and 14 passing and three failing UI scripts.

`AutomaticCompletionTests.InvalidCompletionDependenciesAreRejected` used static reflection for a method
that is now an instance method. It now invokes the public `Create` entry point and checks the actual
validation error. All three dependency combinations pass.

Three UI scripts searched for a source fragment with LF-only newlines. On this CRLF checkout, the search
returned `-1`, so they never evaluated the request payload. A shared extraction helper now handles either
line ending and fails explicitly if the payload cannot be found. The repaired scripts exercise autonomy,
build verification, and Jira comment selection successfully.

## Reproduced defects

Final full quality run:

| Check | Result |
| --- | --- |
| Release solution build | Passed; zero warnings, zero errors |
| C# tests | 637 passed, 18 failed, zero skipped; 655 total |
| UI scripts | 18 passed, zero failed |
| CLI process scenarios | Six passed, zero failed |
| Instrumented line coverage | 83.05% (5,739 / 6,910) |
| Instrumented branch coverage | 71.20% |
| API line / branch coverage | 69.24% / 58.01% |
| Core line / branch coverage | 90.53% / 79.57% |

The 18 failing cases correspond to the nine findings below. The baseline's three broken C# harness
cases now pass. Coverage comes from the final Release run; the initial Debug measurement used a different
instrumented line set, so the two percentages are not a strict before/after comparison. The CLI smoke
tests run as separate processes and are not included in these coverage percentages.

Severity here means repair priority: P1 affects approval or trustworthy repository review; P2 affects
supported requests or ordinary repository inspection; P3 affects response caching consistency.

| ID | Priority | Reproduction and actual result | Expected result / regression |
| --- | --- | --- | --- |
| QA-01 | P1 | Reviewer says `I cannot issue REVIEW_APPROVED because the patch has a blocker.`; Release Manager emits the completion marker. Status becomes `completed`. Quoted reviewer markers also approve. | Require an actual approval decision. `WorkflowDecisionScenarioTests.NegatedOrQuotedReviewerMarkersDoNotAuthorizeCompletion` (2 cases). |
| QA-02 | P1 | After a valid review, Release Manager says `Do not emit WORKFLOW_COMPLETE yet; release checks are outstanding.` Status becomes `completed`. A quoted example also completes. | Quoted or negated prose must not authorize completion. `NegatedOrQuotedReleaseMarkersDoNotCompleteTheWorkflow` (2 cases). |
| QA-03 | P1 | Enable Security in a real scripted handoff workflow; Security forwards with `Security assessment remains incomplete.` Release still completes. The positive `SECURITY_APPROVED` control completes as expected. | Enabled Security must explicitly approve before completion. `AgentConversationScenarioTests.EnabledSecurityGateRequiresAnExplicitApproval(false)` (1 case). |
| QA-04 | P1 | Write `src/NewFeature.cs` through the repository tool. Direct reading and diff see it, but listing, search, and C# detection do not. | All inspection tools must see newly created source before commit. `RepositoryScenarioTests.NewlyWrittenSourceIsVisibleToAllInspectionTools` (3 failing cases; diff control passes). |
| QA-05 | P1 | Create a Persian-named `.cs` file with Git's normal `core.quotePath=true`. Listing and search omit the staged file; diff omits the untracked file. | Parse Git filenames without dropping Unicode paths. `UnicodeSourcePathsRemainDiscoverable` (3 cases). |
| QA-06 | P2 | Stage 1,501 ordinary files followed by `z-target.cs`; unfiltered search returns `No matches.` for a symbol in that last file. | Search must not inherit the 1,500-entry presentation cap. `SearchFindsSourceBeyondTheListPresentationLimit` (1 case). |
| QA-07 | P2 | Stage two source files, delete the first from disk, then search for text in the remaining file. Search throws `Source file does not exist`. | A deleted tracked path should not prevent searching the remaining repository. `DeletedTrackedFileDoesNotPreventSearchingRemainingFiles` (1 case). |
| QA-08 | P2 | POST an otherwise valid session with any required agent's configuration set to JSON `null`. `ApplyConfiguredProviderKey` throws `NullReferenceException` before validation. | Return a structured HTTP 400. TestServer propagates the uncaught exception; this is not a validated 400 response. `ApiContractTests.NullAgentConfigurationsAreValidationErrors` (4 cases). |
| QA-09 | P3 | GET `/` succeeds but lacks `Cache-Control: no-store`; `/app.js` and `/styles.css` pass the same cache policy check. | Apply the workbench's configured no-cache policy consistently to its entry page. `WorkbenchAssetsAreServedWithNoCache("/")` (1 case). |

The gate defects are exercised with controlled transcripts/scripted clients; they demonstrate reachable
incorrect decisions, not their frequency with a live model. Repository and null-configuration findings
use actual production methods, not reimplementations.

Recommended repair order: explicit review/release/security decisions; consistent untruncated filename
enumeration; pre-normalization validation of agent values; entry-page cache policy. Re-run the named
regressions after each repair, then the full quality command. The earlier
[workflow reliability audit](workflow-reliability-audit-2026-10-17.md) contains additional architectural
risks that this assessment does not claim to have exhaustively tested.

## Scenario coverage matrix

“Covered” means exercised by the local deterministic suite; it does not imply all cases passed.

| Functionality | Exercised scenarios | Main automated evidence | Remaining boundary |
| --- | --- | --- | --- |
| Agent configuration | Required/optional agents, provider normalization, reasoning/tool capabilities, invalid budgets and autonomy values | `ApiContractTests`, `WorkflowSettingsTests`, `ModelProviderDefinitionTests`, `AgentTeamFactoryTests` | Live model capability drift |
| Workflow orchestration | Jira on/off, security on/off, review/security remediation, rejection, mixed tools/handoffs, fragmented output, stale review, missing approval | `AgentConversationScenarioTests`, `ConversationTerminationTests`, `HandoffTopologyTests`, `WorkflowDecisionScenarioTests` | Model judgment and contradictory natural-language evidence |
| Execution controls | Request/turn/message/cost limits, exact boundaries, repeated state recovery, remediation budgets, provider cancellation | `TokenUsageTests`, `ReviewerRemediationBudgetTests`, `WorkflowLoopGuardTests`, `SessionWorkflowLimitsTests` | Hung OS children; prolonged load and starvation |
| Autonomy and clarification | Five autonomy levels, delegated decisions, follow-up IDs, stale/duplicate answers, cancellation while waiting | `AutonomyPolicyTests`, `SessionClarificationTests`, `JiraClarificationTests`, UI autonomy/clarification scripts | Full browser journey with a live paused run |
| Repository discovery and edits | Tracked/new/ignored/deleted/Unicode paths, large file sets, byte limits, traversal, ambiguous replacements, line endings, fingerprints | `RepositoryScenarioTests`, `RepositoryWorkspaceTests`, `RepositoryTextEditingTests` | OS-specific links, unusual Git filenames, very large repositories |
| Build verification | Actual .NET compile failure/fix, build-skip notices, environment blockers, reviewer retry paths | `RepositoryBuildTests`, `ReviewerBuildGuardChatClientTests`, UI build verification | Missing SDK/feed behavior on clean deployment hosts |
| Completion/publishing | Authorized commit/push/PR dependencies, unsuccessful-run guard, local bare-remote push, provider HTTP contracts | `AutomaticCompletionTests`, `GitHubPullRequestClientTests`, `GitLabMergeRequestClientTests`, `TaskBranchNameTests` | Real host permissions, protected branches, uncertain remote writes |
| Jira | Issue/context/comment selection, report deduplication, clarification, attachments, Unicode decoding, foreign URLs, size limits | `JiraServiceTests`, `JiraAttachmentTests`, reporting/context tests and Jira UI scripts | Real Jira permissions, server versions, attachment-service outages |
| Documents and search | Multipart exact-mode upload, malformed uploads, search/delete, semantic request contracts, Cognia and source routing | `ApiContractTests`, `ReferenceDocumentServiceTests`, `DocumentSearchRouterTests`, `CogniaDocumentSearchServiceTests` | Live embedding quality, Qdrant/Cognia availability and throughput |
| CodeGraph | Tool evidence, missing graph fallback, prefetch behavior, local config/status endpoints | `CodeGraphToolTests`, `CodeGraphPrefetchChatClientTests`, `ApiContractTests` | Real indexer process failure, stale graph/database scale |
| Providers and retry | OpenAI-compatible/AIFA/DeepSeek/Claude configuration, thinking/tool history, retryable status codes, cancellation, streaming usage | Provider/client/retry tests already in the suite | Paid live-provider reliability and rate limits |
| Persistence/accounting | SQLite lifecycle/history, checkpoints, concurrent identical calls, failed calls, replay costs, logs, saved-run reconstruction | Store/checkpoint/usage tests, `ApiContractTests.ArchivedSessionReturnsDurableStatusAndPatch` | Crash/restart reconciliation, writer contention, long-term growth |
| HTTP API | Local GET routes, missing-run actions, JSON binding, required inputs, uploads, error mapping, fake-secret non-disclosure | `ApiContractTests` | Real socket/proxy/TLS behavior; successful session creation through HTTP |
| Browser workbench | Start/stop/restart, review confirmation, diff display, branches, credentials, notices, summaries, diagnostics, payloads | 18 standalone scripts under `tests/ui` | Actual DOM/layout, browser compatibility, accessibility, slow overlapping polling |
| CLI | Both help flags, missing task/model/key, invalid endpoint and exit codes | `tests/cli/cli-contract.test.cjs` | Successful live-model CLI workflow and its terminal exit behavior |

## Re-running and artifacts

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File CSharp/tests/run-quality-checks.ps1
```

The runner builds the entire C# solution, runs all xUnit tests with coverage, then runs UI and CLI checks
even if xUnit has found defects. It exits nonzero if any group fails. It does not use a filter to hide the
open regressions.

Outputs under `CSharp/artifacts/quality-checks/`:

- `tests.trx`: per-case results, durations, assertions and stack traces.
- `*/coverage.cobertura.xml`: measured API/Core line and branch coverage.
- `ui-results.json`: each script's outcome and duration.
- `summary.json`: build and suite exit codes.

Original baseline and focused reproductions are under `CSharp/artifacts/test-audit/`.
Generated artifacts are local and ignored by Git; the assessment and test sources are the durable record.

To reproduce just the newly added C# scenarios:

```powershell
dotnet test CSharp/tests/Xbank.DevAgent/Xbank.DevAgent.Tests/Xbank.DevAgent.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ApiContractTests|FullyQualifiedName~RepositoryScenarioTests|FullyQualifiedName~WorkflowDecisionScenarioTests|FullyQualifiedName~EnabledSecurityGateRequiresAnExplicitApproval"
```

## Validation still required before claiming complete coverage

Use disposable service accounts/repositories for real GitHub/GitLab/Jira publishing and authenticated
attachment reads. Run browser automation for the complete setup → run → clarification → review → publish
journey, including refresh/restart, slow responses, multiple tabs, keyboard access and mobile layout.
Exercise forced process termination, restart mid-tool, SQLite writer contention, hung Git/build/indexer
children, and sustained concurrent sessions with explicit latency and resource budgets. Sample live model
workflows to evaluate planning/review quality and the usefulness of handoff evidence. These scenarios were
not executed by this local suite.
