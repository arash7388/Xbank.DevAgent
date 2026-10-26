using Maf.Handoff.Api;
using Maf.Handoff.Core;
using Maf.Handoff.Talk;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddTalkNotifications(builder.Configuration);
builder.Services.AddHttpClient<GitHubPullRequestClient>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("maf-repository-workbench/1.0");
});
builder.Services.AddHttpClient<GitLabMergeRequestClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("maf-repository-workbench/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<JiraService>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));
builder.Services.AddHttpClient("qdrant", client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddHttpClient("embeddings", client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient("cognia", client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("maf-repository-workbench/1.0");
});
builder.Services.AddHttpClient<DeepSeekPricingService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("maf-repository-workbench/1.0");
});
builder.Services.AddSingleton<ReferenceDocumentService>();
builder.Services.AddSingleton<CogniaDocumentSearchService>();
builder.Services.AddSingleton<SqliteLlmCheckpointStore>(_ =>
{
    string configuredPath = builder.Configuration["Checkpointing:DatabasePath"] ??
        Path.Combine("data", "llm-checkpoints.db");
    string databasePath = Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredPath);
    return new SqliteLlmCheckpointStore(databasePath);
});
builder.Services.AddSingleton<ILlmCheckpointStore>(services => services.GetRequiredService<SqliteLlmCheckpointStore>());
builder.Services.AddSingleton<IWorkflowRunStore>(services => services.GetRequiredService<SqliteLlmCheckpointStore>());
builder.Services.AddSingleton<RepositorySessionService>();
builder.Services.AddHttpClient<RunConnectivityCheck>(client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<CodeGraphService>();

WebApplication app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // The client disconnected. There is no response stream left to write to.
    }
    catch (KeyNotFoundException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status404NotFound, exception.Message);
    }
    catch (Exception exception) when (exception is ArgumentException or ConfigurationException or Microsoft.Data.Sqlite.SqliteException)
    {
        await WriteErrorAsync(context, StatusCodes.Status400BadRequest, exception.Message);
    }
    catch (Exception exception) when (
        exception is RepositoryException or InvalidOperationException)
    {
        await WriteErrorAsync(context, StatusCodes.Status409Conflict, exception.Message);
    }
    catch (RunConnectivityException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, exception.Message);
    }
    catch (HttpRequestException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
            $"The configured remote service could not be reached: {exception.Message}");
    }
});

app.UseDefaultFiles();
StaticFileOptions workbenchFiles = new()
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    },
};
app.UseStaticFiles(workbenchFiles);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", provider = "openai-compatible" }));

app.MapGet("/api/agents/default-prompts", () => Results.Ok(AgentPrompts.Defaults));

app.MapGet("/api/agents/tools", () => Results.Ok(AgentToolCatalog.ByAgent));

app.MapGet("/api/jira/issues/{key}", async (string key, JiraService jira, CancellationToken cancellationToken) =>
    Results.Ok(new { key, summary = await jira.GetIssueTitleByKeyAsync(key, cancellationToken) }));

app.MapGet("/api/jira/search", async (string jql, int? maxResults, JiraService jira, CancellationToken cancellationToken) =>
    Results.Ok(await jira.SearchIssuesAsync(jql, maxResults ?? 10, cancellationToken)));

app.MapGet("/api/jira/projects/{projectKey}/issues", async (string projectKey, int? maxResults, JiraService jira, CancellationToken cancellationToken) =>
    Results.Ok(await jira.GetTasksByProjectAsync(projectKey, maxResults ?? 20, cancellationToken)));

app.MapGet("/api/jira/text-search", async (string term, int? maxResults, JiraService jira, CancellationToken cancellationToken) =>
    Results.Ok(await jira.GetTasksByTextSearchAsync(term, maxResults ?? 20, cancellationToken)));

app.MapGet("/api/jira/issues/{key}/attachments", async (string key, JiraService jira, CancellationToken cancellationToken) =>
    Results.Ok(await jira.GetAttachmentsAsync(key, cancellationToken)));

app.MapGet("/api/jira/issues/{key}/attachments/{attachmentId}/text", async (string key, string attachmentId, int? offset, JiraService jira, CancellationToken cancellationToken) =>
    Results.Ok(await jira.ReadAttachmentAsync(key, attachmentId, offset ?? 0, cancellationToken)));

app.MapGet("/api/jira/settings", (IConfiguration configuration) =>
    Results.Ok(new { user = configuration["Jira:User"] ?? "", baseUrl = configuration["Jira:BaseUrl"] ?? "", authMode = configuration["Jira:AuthMode"] ?? "Basic" }));

app.MapGet("/api/jira/myself", async (JiraService jira, CancellationToken cancellationToken) =>
    Results.Ok(await jira.GetCurrentUserAsync(cancellationToken)));

app.MapGet("/api/jira/issues/{key}/details", async (string key, JiraService jira, CancellationToken cancellationToken) =>
    Results.Ok(await jira.GetIssueDetailsAsync(key, cancellationToken)));

app.MapPost("/api/jira/issues", async (JiraCreateIssueRequest request, JiraService jira, CancellationToken cancellationToken) =>
{
    string key = await jira.CreateIssueAsync(request, cancellationToken);
    return Results.Created($"/api/jira/issues/{key}/details", new { key });
});

app.MapPost("/api/jira/issues/{key}/comments", async (string key, JiraCommentRequest request, JiraService jira, CancellationToken cancellationToken) =>
    Results.Ok(await jira.PostCommentAsync(key, request.Comment, cancellationToken)));

app.MapGet("/api/providers/configured", (IConfiguration configuration) => Results.Ok(new
{
    deepseek = !string.IsNullOrWhiteSpace(configuration["Providers:DeepSeek:ApiKey"]),
    aifa = !string.IsNullOrWhiteSpace(configuration["Providers:AIFA:ApiKey"]),
    gitlabToken = !string.IsNullOrWhiteSpace(configuration["GitLab:Token"]),
}));

app.MapGet("/api/projects", (IConfiguration configuration) =>
{
    ProjectRepositoryInfo[] projects = configuration.GetSection("Projects")
        .GetChildren()
        .Select(project => new ProjectRepositoryInfo(
            project["Key"] ?? project.Key,
            project["GitLabProjectPath"]))
        .Where(project => !string.IsNullOrWhiteSpace(project.Key))
        .OrderBy(project => project.Key, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Ok(projects);
});

app.MapGet("/api/codegraph/config", (CodeGraphService codeGraph) => Results.Ok(codeGraph.GetConfiguration()));

app.MapPost("/api/repositories/branches", async (
    RepositoryBranchesRequest request, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    RepositoryProvider provider = request.SourceControl?.Trim().ToLowerInvariant() switch
    {
        "github" => RepositoryProvider.GitHub,
        "gitlab" => RepositoryProvider.GitLab,
        _ => throw new ArgumentException("Source control must be either 'github' or 'gitlab'."),
    };
    string? token = string.IsNullOrWhiteSpace(request.AccessToken)
        ? configuration[provider == RepositoryProvider.GitHub ? "GitHub:Token" : "GitLab:Token"]
        : request.AccessToken.Trim();
    return Results.Ok(await RepositoryWorkspace.ListBranchesAsync(
        request.RepositoryUrl.Trim(), token, cancellationToken, provider, configuration["GitLab:Host"]));
});

app.MapPost("/api/codegraph/index", (CodeGraphIndexRequest request, CodeGraphService codeGraph) =>
    Results.Accepted("/api/codegraph/index/status", codeGraph.StartIndex(request)));

app.MapGet("/api/codegraph/index/status", (CodeGraphService codeGraph) => Results.Ok(codeGraph.GetIndexStatus()));

app.MapGet("/api/codegraph/freshness", (string dbPath, string sourcePath, CodeGraphService codeGraph,
    CancellationToken cancellationToken) => Results.Ok(codeGraph.GetFreshness(dbPath, sourcePath, cancellationToken)));

app.MapGet("/api/codegraph/database", (string dbPath, CodeGraphService codeGraph) =>
    Results.Ok(codeGraph.GetDatabaseOverview(dbPath)));

app.MapGet("/api/codegraph/tables/{table}", (string table, string dbPath, int? limit, int? offset, string? search, CodeGraphService codeGraph) =>
    Results.Ok(codeGraph.GetTable(dbPath, table, limit ?? 50, offset ?? 0, search)));

app.MapPost("/api/codegraph/tools/find-symbol", (CodeGraphFindSymbolRequest request, CodeGraphService codeGraph) =>
    Results.Ok(codeGraph.FindSymbol(request)));

app.MapPost("/api/codegraph/tools/get-neighbors", (CodeGraphNeighborsRequest request, CodeGraphService codeGraph) =>
    Results.Ok(codeGraph.GetNeighbors(request)));

app.MapPost("/api/codegraph/tools/get-context", (CodeGraphContextRequest request, CodeGraphService codeGraph) =>
    Results.Ok(new { content = codeGraph.GetContext(request) }));

app.MapPost("/api/codegraph/tools/get-context-by-id", (CodeGraphContextByIdRequest request, CodeGraphService codeGraph) =>
    Results.Ok(new { content = codeGraph.GetContextById(request) }));

app.MapPost("/api/providers/validate", (ModelProviderDefinition provider) =>
    Results.Ok(provider.NormalizeAndValidate()));

app.MapGet("/api/providers/deepseek/pricing", async (
    DeepSeekPricingService pricing,
    CancellationToken cancellationToken) => Results.Ok(await pricing.GetCurrentAsync(cancellationToken)));

app.MapPost("/api/reference-collections", async (
    HttpRequest request,
    ReferenceDocumentService documents,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        throw new ArgumentException("Reference documents must be uploaded as multipart form data.");
    }

    IFormCollection form = await request.ReadFormAsync(cancellationToken);
    ReferenceCollectionInfo collection = await documents.CreateAsync(
        form.Files,
        form["embeddingProvider"].ToString(),
        form["embeddingApiKey"].ToString(),
        form["qdrantUrl"].ToString(),
        form["qdrantApiKey"].ToString(),
        cancellationToken,
        form["searchMode"].ToString());
    return Results.Created($"/api/reference-collections/{collection.Id}", collection);
});

app.MapPost("/api/runs/preflight", async (Dictionary<string, AgentModelConfiguration> agents,
    RepositorySessionService sessions, CancellationToken cancellationToken) =>
{
    await sessions.PreflightAsync(agents, cancellationToken);
    return Results.Ok(new { status = "ok" });
});

app.MapPost("/api/sessions", async (CreateSessionRequest request, RepositorySessionService sessions, CancellationToken cancellationToken) =>
{
    SessionSnapshot snapshot = await sessions.CreateAsync(request, cancellationToken);
    return Results.Accepted($"/api/sessions/{snapshot.Id}", snapshot);
});

app.MapGet("/api/sessions/{id:guid}", (Guid id, RepositorySessionService sessions) =>
    Results.Ok(sessions.Get(id)));

app.MapGet("/api/runs", (IWorkflowRunStore runs, int? limit) => Results.Ok(runs.ListRuns(limit ?? 100)));
app.MapGet("/api/runs/{id:guid}", (Guid id, IWorkflowRunStore runs) => Results.Ok(runs.GetLog(id)));

app.MapGet("/api/sessions/{id:guid}/logs", (Guid id, RepositorySessionService sessions, IWorkflowRunStore runs) =>
{
    return Results.Ok(runs.GetLog(id));
});

app.MapGet("/api/sessions/{id:guid}/report", (Guid id, RepositorySessionService sessions, IWorkflowRunStore runs) =>
    Results.Ok(new
    {
        session = sessions.Get(id),
        database = runs.GetLog(id),
    }));

app.MapPost("/api/sessions/{id:guid}/cancel", (Guid id, RepositorySessionService sessions) =>
{
    sessions.Cancel(id);
    return Results.Ok(sessions.Get(id));
});

app.MapPost("/api/sessions/{id:guid}/clarify", (Guid id, ClarifySessionRequest request, RepositorySessionService sessions) =>
    Results.Ok(sessions.Clarify(id, request)));

app.MapPost("/api/sessions/{id:guid}/modify", (Guid id, ModifySessionRequest request, RepositorySessionService sessions) =>
    Results.Accepted($"/api/sessions/{id}", sessions.RequestChanges(id, request)));

app.MapPost("/api/sessions/{id:guid}/reject", (Guid id, RepositorySessionService sessions) =>
{
    sessions.Reject(id);
    return Results.Ok(sessions.Get(id));
});

app.MapPost("/api/sessions/{id:guid}/approve", async (
    Guid id,
    ApproveSessionRequest request,
    RepositorySessionService sessions,
    CancellationToken cancellationToken) =>
    Results.Ok(await sessions.ApproveAsync(id, request, cancellationToken)));

app.MapPost("/api/sessions/{id:guid}/pull-request", async (
    Guid id,
    RepositorySessionService sessions,
    CancellationToken cancellationToken) =>
    Results.Ok(await sessions.CreatePullRequestAsync(id, cancellationToken)));

app.MapFallbackToFile("index.html", workbenchFiles);

app.Run();

static async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
{
    if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
    {
        return;
    }

    context.Response.StatusCode = statusCode;
    try
    {
        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // The request was aborted between the preflight check and the response flush.
    }
}

public partial class Program;

public sealed record JiraCommentRequest(string Comment);
public sealed record RepositoryBranchesRequest(string RepositoryUrl, string SourceControl, string? AccessToken);
