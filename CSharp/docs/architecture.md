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

1. The API validates the configured agent provider/model/reasoning profiles and creates an opaque session
   identifier.
2. Git validates the repository and branch and clones into a random directory under the configured
   temporary workspace.
3. When enabled, Jira assesses task readiness with full issue details, paginated comments, selected attachments,
   scoped read-only repository inspection, and reference-document search. It checks requirement clarity and
   consistency, code feasibility, dependencies, and integration risks before handing evidence to Planner.
   Blocking clarification ends the run with `input_required`; the user can clarify and start a new run.
   Planner, developer, reviewer, security, and release manager then use their scoped repository tools.
4. Agent events and the current state are exposed through session polling without returning secrets.
5. The UI renders the exact diff and pauses at `awaiting_approval`.
6. Reject leaves the clone uncommitted. Approve creates a new branch and commit; push requires a
   separate checkbox and a GitHub token.

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
