using ModelContextProtocol.Protocol;
using XBank.CodeGraph.McpServer.Retrieval;
using XBank.CodeGraph.McpServer.Tools;

var builder = WebApplication.CreateBuilder(args);

var dbPath = args.Length > 0
    ? args[0]
    : builder.Configuration["CodeGraph:Database"] ?? "codegraph.db";

// ثبت سرویس‌ها

builder.Services.AddSingleton(new GraphStore(dbPath));

builder.Services.AddSingleton<ContextRetriever>();

// افزودن MCP به عنوان Web API

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation
    {
        Name = "CSharpGraph",
        Version = "1.0.0"
    };
})
.WithHttpTransport()
.WithTools<GraphTools>();

var app = builder.Build();

app.MapMcp();

app.MapGet("/", () => "MCP Server is running.");

app.Run();
