using Maf.Handoff.Api;
using Maf.Handoff.Core;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<GitHubPullRequestClient>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("maf-repository-workbench/1.0");
});
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));
builder.Services.AddHttpClient("qdrant", client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddHttpClient("embeddings", client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<DeepSeekPricingService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("maf-repository-workbench/1.0");
});
builder.Services.AddSingleton<ReferenceDocumentService>();
builder.Services.AddSingleton<ILlmCheckpointStore>(_ =>
{
    string configuredPath = builder.Configuration["Checkpointing:DatabasePath"] ??
        Path.Combine("data", "llm-checkpoints.db");
    string databasePath = Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredPath);
    return new SqliteLlmCheckpointStore(databasePath);
});
builder.Services.AddSingleton<RepositorySessionService>();
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
    catch (HttpRequestException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
            $"The configured document service could not be reached: {exception.Message}");
    }
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    },
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", provider = "openai-compatible" }));

app.MapGet("/api/agents/default-prompts", () => Results.Ok(AgentPrompts.Defaults));

app.MapGet("/api/agents/tools", () => Results.Ok(AgentToolCatalog.ByAgent));

app.MapGet("/api/codegraph/config", (CodeGraphService codeGraph) => Results.Ok(codeGraph.GetConfiguration()));

app.MapPost("/api/codegraph/index", (CodeGraphIndexRequest request, CodeGraphService codeGraph) =>
    Results.Accepted("/api/codegraph/index/status", codeGraph.StartIndex(request)));

app.MapGet("/api/codegraph/index/status", (CodeGraphService codeGraph) => Results.Ok(codeGraph.GetIndexStatus()));

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
        cancellationToken);
    return Results.Created($"/api/reference-collections/{collection.Id}", collection);
});

app.MapPost("/api/sessions", (CreateSessionRequest request, RepositorySessionService sessions) =>
{
    SessionSnapshot snapshot = sessions.Create(request);
    return Results.Accepted($"/api/sessions/{snapshot.Id}", snapshot);
});

app.MapGet("/api/sessions/{id:guid}", (Guid id, RepositorySessionService sessions) =>
    Results.Ok(sessions.Get(id)));

app.MapPost("/api/sessions/{id:guid}/cancel", (Guid id, RepositorySessionService sessions) =>
{
    sessions.Cancel(id);
    return Results.Ok(sessions.Get(id));
});

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

app.MapFallbackToFile("index.html");

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
