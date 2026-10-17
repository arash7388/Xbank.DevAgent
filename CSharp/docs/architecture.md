# Repository Change Architecture

```mermaid
flowchart TB
    UI[Browser workbench] -->|create session| API[ASP.NET Core API]
    API --> SS[In-memory session service]
    SS -->|validated HTTPS URL| GIT[Git process boundary]
    GIT --> CLONE[Isolated temporary clone]

    SS --> RUN[Handoff workflow runner]
    RUN --> PROFILES[Six agent model profiles]
    PROFILES --> PROVIDER[Provider validation and URL normalization]
    PROVIDER --> CLIENT[One IChatClient per agent]
    CLIENT --> OPENAI[OpenAI / DeepSeek chat completions]
    CLIENT --> CLAUDE[Official Anthropic C# IChatClient]
    RUN --> FLOW[Microsoft Agent Framework HandoffBuilder]

    subgraph Agents
      J --> P[Planner]
      P --> D[Developer]
      D --> R[Reviewer]
      R -->|changes| D
      R -->|approved| S[Security]
      S -->|remediate| D
      S -->|approved| RM[Release manager]
    end

    FLOW -->|when enabled| J
    FLOW -->|when Jira is disabled| P
    J --> READ
    P --> READ[Read-only repository tools]
    R --> READ
    S --> READ
    RM --> READ
    D --> EDIT[Read and write repository tools]
    READ --> CLONE
    EDIT --> CLONE

    RM -->|terminal decision| DIFF[Unified Git diff]
    CLONE --> DIFF
    DIFF --> UI
    UI -->|reject| REJECT[No commit]
    UI -->|approve| COMMIT[New local branch and commit]
    COMMIT -->|optional explicit push| REMOTE[New GitHub branch]
```

## Lifecycle

Each repository session persists `AutonomyLevel` (0/25/50/75/100, default 100) and separate `AutoCommit`,
`AutoPush`, and `AutoCreatePullRequest` authorizations. The API validates completion dependencies and
propagates the autonomy policy to every agent's `WorkflowSettings`. `AutonomyUserInput` supplies the
generic decision tool and Jira clarification path: at 100% it immediately returns delegated-decision
guidance and records the question, without entering `awaiting_input`. Other levels use the same
session question/answer lifecycle; policy instructions define when the agent should ask.

After successful review, upfront authorizations trigger the existing commit/push and PR/MR methods.
`AutomaticCompletionPending` keeps browser polling active through intermediate publishing states.
Without automatic commit, 100% runs finish as `completed` with the diff available; lower levels retain
`awaiting_approval`. Completed patches can still be explicitly approved later. Autonomy and completion
settings are recorded in durable configuration and the selected autonomy level is included in snapshots.
Review/build outcomes, execution limits, and cancellation retain their existing enforcement.

1. The API validates the configured agent provider/model/reasoning profiles and creates an opaque session
   identifier.
2. Git validates the repository and branch and clones into a random directory under the configured
   temporary workspace. Before agents start, Git pulls the selected base branch from `origin` with
   `--ff-only --no-rebase`. A failed pull stops startup before any agent work begins.
3. When enabled, Jira assesses task readiness with full issue details, paginated comments, selected attachments,
   scoped read-only repository inspection, and reference-document search. It checks requirement clarity and
   consistency, code feasibility, dependencies, and integration risks before handing evidence to Planner.
   It can publish one consolidated Persian problem comment on the selected task. A durable `jira_problem`
   event records the actual posting outcome and drives the UI notice; repeated attempts reuse that outcome.
   Below 100% autonomy, clarification pauses the active session at `awaiting_input`. The UI submits a question ID and answer to
   `POST /api/sessions/{id}/clarify`; the same agent resumes with that answer and the existing clone.
   The execution timeout pauses while waiting, and cancellation still stops the run. Marker-based
   assessments from custom prompts are intercepted before workflow termination; retries retain request budgets.
   Developer posts separate Persian scope-change comments on the bound task, with deduplicated outcomes
   and durable `jira_scope_change` events when the Jira agent is enabled. Disabled Jira runs receive no
   Jira tools or reporting clients and do not load Jira attachments.
   Planner, developer, reviewer, security, and release manager then use their scoped repository tools.
   Jira and Planner reads are compacted for reconnaissance. Developer reads preserve the source text,
   including whitespace and line endings, for exact edits. Text replacements require a unique match,
   treating only LF/CRLF/CR line endings as equivalent; they preserve local line endings, UTF-8 BOM,
   and unrelated content. This avoids failed edits caused by copying normalized text into a CRLF file.
4. Agent events and the current state are exposed through session polling without returning secrets.
5. The UI renders the exact diff. Without automatic commit, lower autonomy levels pause at
   `awaiting_approval`; 100% finishes as `completed`. Selected automatic completion actions run immediately.
6. Reject leaves an awaiting-approval clone uncommitted. Approve creates a new branch and commit;
   push requires an explicit selection and a repository token. PR/MR creation can also be authorized upfront.

## Trust boundaries

- Repository content, model output, and user instructions are untrusted.
- Prompt rules guide models but are not the security boundary; tool path and size checks are.
- Git is invoked with structured argument lists and without a shell.
- GitHub credentials are process-environment values and are never embedded in repository URLs.
- This local sample stores sessions and secrets in memory and has no authentication or durable store.
- Arbitrary repository command execution is deliberately unavailable because cloned code is untrusted.
- Provider definitions are untrusted input. HTTPS, URL shape, token metadata, tool calling, and
  reasoning-effort compatibility are validated before a repository session starts.
- Provider keys can be reused by profiles assigned to the same provider, but each agent receives a
  separately constructed client and its own chat options.
