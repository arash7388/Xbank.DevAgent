using Microsoft.AspNetCore.DataProtection;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;
using Xbank.DevAgent.Talk;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
// Providers:AIFA:Endpoint can come from user secrets or appsettings; AIFA_ENDPOINT still wins at runtime.
AifaRequestContext.ConfigureEndpoint(builder.Configuration["Providers:AIFA:Endpoint"]);
string profileDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xbank.DevAgent");
DeveloperSettingsStore developerSettings = new(Path.Combine(profileDirectory, "settings.protected"),
    DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(profileDirectory, "keys")), protection =>
    {
        protection.SetApplicationName("Xbank.DevAgent");
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
    })
        .CreateProtector("Xbank.DevAgent.DeveloperSettings.v1"));
((IConfigurationBuilder)builder.Configuration).Add(developerSettings);
builder.Services.AddSingleton(developerSettings);
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
builder.Services.AddSingleton<CodingRuleStore>(_ => new CodingRuleStore(
    Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["CodingRules:DatabasePath"] ?? "data/coding-rules.db")));
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
    catch (System.ClientModel.ClientResultException exception)
    {
        string detail = exception.Message.ReplaceLineEndings(" ").Trim();
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ModelProvider")
            .LogWarning(exception, "Model provider request failed with status {Status}.", exception.Status);
        await WriteErrorAsync(context, StatusCodes.Status502BadGateway,
            $"The model provider rejected the request: {(detail.Length > 800 ? detail[..800] : detail)}");
    }
    catch (Exception exception)
    {
        // Always answer with JSON so browser clients never receive an HTML or plain-text error page.
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("UnhandledRequest")
            .LogError(exception, "Unhandled exception for {Method} {Path}.", context.Request.Method, context.Request.Path);
        await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
            "The server could not complete the request. Check the API console for details.");
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

app.MapGet("/api/settings", (DeveloperSettingsStore store, IConfiguration configuration, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(store.Read(configuration));
});
app.MapPut("/api/settings", (DeveloperSettings request, DeveloperSettingsStore store, IConfiguration configuration) =>
{
    store.Save(request, configuration);
    return Results.NoContent();
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", provider = "openai-compatible" }));

app.MapGet("/api/agents/default-prompts", () => Results.Ok(AgentPrompts.Defaults));

app.MapPost("/api/task-analysis", async (TaskAnalysisRequest request, JiraService jira,
    IConfiguration configuration, CancellationToken cancellationToken) =>
    Results.Ok(await new TaskAnalysisService(jira, configuration).AnalyseAsync(request, cancellationToken)));

app.MapGet("/api/agents/tools", () => Results.Ok(AgentToolCatalog.ByAgent));

app.MapGet("/api/coding-rules", (CodingRuleStore rules) => Results.Ok(rules.List()));
app.MapPost("/api/coding-rules", (SaveCodingRuleRequest request, CodingRuleStore rules) =>
{
    CodingRule rule = rules.Save(null, request);
    return Results.Created($"/api/coding-rules/{rule.Id}", rule);
});
app.MapPut("/api/coding-rules/{id:guid}", (Guid id, SaveCodingRuleRequest request, CodingRuleStore rules) =>
    Results.Ok(rules.Save(id, request)));
app.MapPut("/api/coding-rules/{id:guid}/enabled", (Guid id, SetCodingRuleEnabledRequest request, CodingRuleStore rules) =>
{
    rules.SetEnabled(id, request.Enabled);
    return Results.NoContent();
});
app.MapDelete("/api/coding-rules/{id:guid}", (Guid id, CodingRuleStore rules) =>
{
    rules.Delete(id);
    return Results.NoContent();
});

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
    aifaEndpoint = AifaRequestContext.Endpoint,
    gitlabToken = !string.IsNullOrWhiteSpace(configuration["GitLab:Token"]) ||
        DeveloperSettingsStore.Projects(configuration).Any(project => !string.IsNullOrWhiteSpace(project["Token"])),
}));

app.MapGet("/api/projects", (IConfiguration configuration) =>
{
    ProjectRepositoryInfo[] projects = DeveloperSettingsStore.Projects(configuration)
        .Select(project => new ProjectRepositoryInfo(
            project["Key"] ?? project.Key,
            project["GitLabProjectPath"]))
        .Where(project => !string.IsNullOrWhiteSpace(project.Key))
        .OrderBy(project => project.Key, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Ok(projects);
});

app.MapGet("/api/codegraph/config", (CodeGraphService codeGraph) => Results.Ok(codeGraph.GetConfiguration()));

app.MapPost("/api/repositories/merge-requests", async (
    RepositoryBranchesRequest request, IConfiguration configuration, GitLabMergeRequestClient client,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(request.SourceControl, "gitlab", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Merge request selection requires GitLab.");
    string token = string.IsNullOrWhiteSpace(request.AccessToken)
        ? DeveloperSettingsStore.GitLabToken(configuration, request.RepositoryUrl) ?? "" : request.AccessToken.Trim();
    return Results.Ok(await client.ListOpenAsync(request.RepositoryUrl.Trim(), token, cancellationToken));
});

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
        ? (provider == RepositoryProvider.GitHub ? configuration["GitHub:Token"] : DeveloperSettingsStore.GitLabToken(configuration, request.RepositoryUrl))
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

app.MapGet("/api/runs", (IWorkflowRunStore runs, int? limit, int? offset) => Results.Ok(runs.ListRuns(limit ?? 50, offset ?? 0)));
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
