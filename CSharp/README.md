# Repository Change Workbench

A .NET 10 sample that connects Microsoft Agent Framework 1.19 handoff agents to a GitHub
repository. The agents inspect and edit an isolated clone, then the browser shows the exact Git diff
and requires user confirmation before any commit or push.

No Azure services or Azure packages are used. The workbench supports OpenAI through Chat
Completions, DeepSeek through its provider-native Microsoft Agent Framework adapter, and Claude
through Anthropic's official C# `IChatClient` integration. The DeepSeek adapter preserves
`reasoning_content` across tool calls and agent handoffs.

Provider definitions include model identity, URL, tool-calling/vision/thinking capabilities, token
limits, and supported reasoning-effort values. Full `/chat/completions` URLs are normalized to the
SDK base endpoint. DeepSeek agents preserve the selected reasoning level and retain full thinking state;
OpenAI reasoning values are passed through its Chat Completions options. Tool calling is mandatory
for this handoff topology.

Each agent has an independent OpenAI, DeepSeek, or Claude provider selector, model selector, and
reasoning-effort selector. All agents default to DeepSeek `deepseek-v4-flash` with high reasoning,
but can be configured individually. Provider credentials are entered once and reused only by agents
assigned to that provider. Endpoints and capability metadata remain internal.

At the end of every run, the activity view and session response show provider-reported input/output
tokens for every participating agent and the run total. To calculate an estimated USD cost, enter the
current input and output price per million tokens for each agent model in **Usage pricing**. DeepSeek
models are prefilled with their peak, cache-miss rates because the provider usage response does not
separate cache hits or pricing time bands; these defaults remain editable. When a rate or provider
usage metadata is unavailable, the workbench reports the token count without inventing a dollar amount.

## Safety model

- Only HTTPS `github.com/{owner}/{repository}` URLs are accepted.
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

### Reference-document RAG

Start Qdrant locally before attaching reference files:

```powershell
docker compose -f docker-compose.qdrant.yml up -d
```

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

The UI requests:

1. GitHub repository URL and base branch.
2. GitHub token for a private clone, optional push, or pull-request creation.
3. AI provider, model, and reasoning effort for each agent, plus keys for selected providers.
4. A concrete code-change instruction with expected behavior and tests.

To push an approved branch, the GitHub token needs repository Contents read/write access. The
Generate PR action also needs Pull requests read/write access. It pushes a locally committed agent
branch when necessary, then creates a pull request into the selected base branch. For a public
repository with no push or pull request, the GitHub token may be omitted. Provider API keys are
always required and are never reused as GitHub credentials.

The session API accepts an `agents` object keyed by the stable names `intake`, `jira`, `planner`,
`developer`, `reviewer`, `security`, and `release_manager`. Every entry contains an independent
`provider` definition and `apiKey`. Intake, developer, and release manager are required; Jira,
planner, reviewer, and security are optional. For example, the planner entry is:

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

A real request supplies the other five agent entries too.

## Projects

- `Maf.Handoff.Core`: agents, handoff workflow, OpenAI-compatible client, Git and safe tools
- `Maf.Handoff.Api`: session API and browser workbench
- `Maf.Handoff.Cli`: advisory CLI using `OPENAI_*` environment variables
- `Maf.Handoff.Tests`: configuration, topology, and repository-boundary tests

See [the architecture](docs/architecture.md).

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
