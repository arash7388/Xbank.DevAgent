using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class SqlServerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepositoryDatabaseOverridesOnlyCatalogAndBlankKeepsDefault(bool advanced)
    {
        SqlServerSettings settings = new(Server: "shared-server", Database: "DefaultDb", UseConnectionString: advanced);
        const string raw = "Server=shared-server;Database=DefaultDb;Integrated Security=True;Encrypt=True;";
        const string database = "Repo;User ID=literal";
        SqlServerSettings.Validate(settings, raw, "", database);
        SqlConnectionStringBuilder parsed = new(SqlServerSettings.BuildConnectionString(settings, raw, "", database));
        Assert.Equal(database, parsed.InitialCatalog);
        Assert.Equal("shared-server", parsed.DataSource);
        Assert.True(parsed.IntegratedSecurity);
        Assert.Empty(parsed.UserID);
        Assert.True(parsed.Encrypt);
        parsed = new(SqlServerSettings.BuildConnectionString(settings, raw, "", " "));
        Assert.Equal("DefaultDb", parsed.InitialCatalog);
    }

    [Fact]
    public void WindowsAuthenticationIgnoresSavedSqlCredentialsAndAdvancedConnection()
    {
        SqlServerSettings settings = new(Enabled: true, Username: "old-login", Server: @"SERVER\INSTANCE",
            Database: "MyDatabase", UseConnectionString: false);
        SqlServerSettings.Validate(settings, "invalid legacy connection", "old-password");
        SqlConnectionStringBuilder parsed = new(SqlServerSettings.BuildConnectionString(settings,
            "invalid legacy connection", "old-password"));
        Assert.Equal(@"SERVER\INSTANCE", parsed.DataSource);
        Assert.Equal("MyDatabase", parsed.InitialCatalog);
        Assert.True(parsed.IntegratedSecurity);
        Assert.Empty(parsed.UserID);
        Assert.Empty(parsed.Password);
        Assert.True(parsed.Encrypt);
        Assert.False(parsed.TrustServerCertificate);
    }

    [Fact]
    public void StructuredSqlAuthenticationRequiresLoginAndPreservesPassword()
    {
        SqlServerSettings settings = new(Enabled: true, Server: "sql.test,1433", Database: "app",
            UseWindowsAuthentication: false, UseConnectionString: false);
        Assert.Throws<ArgumentException>(() => SqlServerSettings.Validate(settings, "", ""));
        settings = settings with { Username = " reader " };
        SqlServerSettings.Validate(settings, "", " secret ");
        SqlConnectionStringBuilder parsed = new(SqlServerSettings.BuildConnectionString(settings, "", " secret "));
        Assert.False(parsed.IntegratedSecurity);
        Assert.Equal("reader", parsed.UserID);
        Assert.Equal(" secret ", parsed.Password);
        Assert.Throws<ArgumentException>(() => SqlServerSettings.Validate(settings with { Database = "" }, "", ""));
    }

    [Fact]
    public void SeparateCredentialsOverrideIntegratedAuthenticationAndPreservePasswordWhitespace()
    {
        string connection = SqlServerSettings.BuildConnectionString(new(Username: "reader"),
            "Server=test;Database=app;Integrated Security=True;", " secret ");
        SqlConnectionStringBuilder parsed = new(connection);
        Assert.False(parsed.IntegratedSecurity);
        Assert.Equal("reader", parsed.UserID);
        Assert.Equal(" secret ", parsed.Password);
        Assert.False(parsed.PersistSecurityInfo);
        Assert.True(parsed.Encrypt);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1001, 30)]
    [InlineData(200, 0)]
    [InlineData(200, 301)]
    public void RejectsInvalidLimits(int rows, int seconds) => Assert.Throws<ArgumentException>(() =>
        SqlServerSettings.Validate(new(MaxRows: rows, CommandTimeoutSeconds: seconds), "", ""));

    [Fact]
    public void CertificateTrustIsOptInAndAdvancedModeKeepsItsOwnSetting()
    {
        SqlServerSettings settings = new(Server: "test", Database: "app", UseConnectionString: false);
        SqlConnectionStringBuilder parsed = new(SqlServerSettings.BuildConnectionString(settings, "", ""));
        Assert.False(parsed.TrustServerCertificate);
        parsed = new(SqlServerSettings.BuildConnectionString(settings with { TrustServerCertificate = true }, "", ""));
        Assert.True(parsed.TrustServerCertificate);
        Assert.True(parsed.Encrypt);
        parsed = new(SqlServerSettings.BuildConnectionString(settings with { UseConnectionString = true, TrustServerCertificate = true },
            "Server=test;Database=app;Integrated Security=True;TrustServerCertificate=False;", ""));
        Assert.False(parsed.TrustServerCertificate);
    }

    [Theory]
    [InlineData("SSL Provider: certificate chain was issued by an authority that is not trusted", "certificate validation failed")]
    [InlineData("Login failed for user", "authentication failed")]
    [InlineData("Cannot open database requested by the login", "could not open")]
    [InlineData("Cannot open database requested by the login. Login failed for user", "could not open")]
    [InlineData("Connection Timeout Expired", "timed out")]
    [InlineData("A network-related error: server was not found", "unreachable")]
    [InlineData("Invalid object name", "object was not found")]
    [InlineData("SELECT permission was denied", "permission was denied")]
    [InlineData("Unexpected failure", "operation failed")]
    public async Task ShowsActionableErrorsWithoutLeakingRawDiagnostics(string diagnostic, string expected)
    {
        await using var server = await ProtocolServer.StartAsync();
        server.Fail = true;
        server.FailureText = diagnostic + ";Password=sentinel-secret;Server=private-host";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.Connection.ExecuteAsync(new("query", Sql: "SELECT 1"), CancellationToken.None));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("sentinel-secret", error.ToString());
        Assert.DoesNotContain("private-host", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task DisabledAccessDoesNotStartAProcess()
    {
        SqlServerService service = new(new ConfigurationBuilder().Build(), new TestEnvironment());
        Assert.Null(await service.OpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartupFailureDoesNotExposeCredentials()
    {
        SqlServerService service = new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlServer:Enabled"] = "true", ["SqlServer:ConnectionString"] = "Server=test;Database=app;Password=sentinel-secret;User ID=reader;",
            ["SqlServer:McpCommand"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"),
        }).Build(), new TestEnvironment());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync(CancellationToken.None));
        Assert.Contains("setup-sql-server", error.Message);
        Assert.DoesNotContain("sentinel-secret", error.ToString());
    }

    [Fact]
    public async Task UsesOnlyDefaultProfileAndPreservesParameterBindingsOverMcp()
    {
        await using var server = await ProtocolServer.StartAsync();
        string result = await server.Connection.ExecuteAsync(new("query", Sql: "SELECT @id AS id",
            Parameters: new() { ["id"] = 42 }), CancellationToken.None);
        using var json = JsonDocument.Parse(result);
        Assert.Equal("run_query", server.Tool);
        Assert.Equal("default", json.RootElement.GetProperty("profile").GetString());
        Assert.Equal(42, json.RootElement.GetProperty("parameters").GetProperty("id").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("catalog", out _));
        Assert.False(json.RootElement.TryGetProperty("snapshot", out _));
    }

    [Fact]
    public async Task QuotesMigrationIdentifiersAndRequestsAllObjectMetadata()
    {
        await using var server = await ProtocolServer.StartAsync();
        string result = await server.Connection.ExecuteAsync(new("migrations", Schema: "audit", Name: "a]; DELETE FROM x;--"), CancellationToken.None);
        using var json = JsonDocument.Parse(result);
        Assert.Equal("SELECT TOP (17) * FROM [audit].[a]]; DELETE FROM x;--] ORDER BY 1 DESC",
            json.RootElement.GetProperty("sql").GetString());
        result = await server.Connection.ExecuteAsync(new("object", Name: "dbo.Users"), CancellationToken.None);
        using var metadata = JsonDocument.Parse(result);
        Assert.Equal("get_object", server.Tool);
        Assert.Equal(4, metadata.RootElement.GetProperty("includes").GetArrayLength());
        result = await server.Connection.ExecuteAsync(new("object", Name: "dbo.DoWork", Kind: "Routine"), CancellationToken.None);
        Assert.Contains("Definition", result);
        result = await server.Connection.ExecuteAsync(new("analyze", Sql: "SELECT 1"), CancellationToken.None);
        Assert.Equal("analyze_query", server.Tool);
        Assert.True(JsonDocument.Parse(result).RootElement.GetProperty("estimated").GetBoolean());
    }

    [Fact]
    public async Task RejectsWriteOperationsAndPropagatesCancellationWithoutRawMcpErrors()
    {
        await using var server = await ProtocolServer.StartAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => server.Connection.ExecuteAsync(new("run_command", Sql: "DELETE FROM Users"), CancellationToken.None));
        Assert.Null(server.Tool);
        server.Fail = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.Connection.ExecuteAsync(new("query", Sql: "SELECT 1"), CancellationToken.None));
        Assert.DoesNotContain("sentinel-secret", error.ToString());
        server.Wait = true;
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.Connection.ExecuteAsync(new("query", Sql: "SELECT 1"), cancelled.Token));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "SqlServerTests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // Exercise the real MCP client and JSON-RPC serialization without a database or subprocess.
    private sealed class ProtocolServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private Task _loop = Task.CompletedTask;
        public SqlServerConnection Connection { get; private set; } = null!;
        public string? Tool { get; private set; }
        public bool Fail { get; set; }
        public string FailureText { get; set; } = "sentinel-secret";
        public bool Wait { get; set; }

        public static async Task<ProtocolServer> StartAsync()
        {
            ProtocolServer server = new();
            Pipe requests = new(), responses = new();
            server._loop = server.RespondAsync(requests.Reader.AsStream(), responses.Writer.AsStream());
            var client = await McpClient.CreateAsync(new StreamClientTransport(requests.Writer.AsStream(), responses.Reader.AsStream()),
                new() { ProtocolVersion = "2025-11-25" }, cancellationToken: server._stop.Token);
            server.Connection = new(client, 2, 17);
            return server;
        }

        private async Task RespondAsync(Stream input, Stream output)
        {
            using var reader = new StreamReader(input);
            await using var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
            try
            {
                while (await reader.ReadLineAsync(_stop.Token) is string line)
                {
                    JsonObject request = JsonNode.Parse(line)!.AsObject();
                    if (request["id"] is not { } id) continue;
                    string method = request["method"]!.GetValue<string>();
                    JsonNode result;
                    if (method == "initialize") result = JsonSerializer.SerializeToNode(new
                    {
                        protocolVersion = "2025-11-25", capabilities = new { tools = new { } },
                        serverInfo = new { name = "test", version = "1" },
                    })!;
                    else
                    {
                        Tool = request["params"]!["name"]!.GetValue<string>();
                        if (Wait) continue;
                        result = new JsonObject
                        {
                            ["isError"] = Fail,
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Fail ? FailureText : "{}" }),
                            ["structuredContent"] = request["params"]!["arguments"]!.DeepClone(),
                        };
                    }
                    await writer.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString());
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await Connection.DisposeAsync();
            await _loop;
            _stop.Dispose();
        }
    }
}
