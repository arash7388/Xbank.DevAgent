using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Api;

public sealed class SqlServerService(IConfiguration configuration, IHostEnvironment environment)
{
    public string Command
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(configuration["SqlServer:McpCommand"]))
                return configuration["SqlServer:McpCommand"]!;
            string executable = OperatingSystem.IsWindows() ? "mcp-mssql.exe" : "mcp-mssql";
            for (DirectoryInfo? directory = new(environment.ContentRootPath); directory is not null; directory = directory.Parent)
            {
                string path = Path.Combine(directory.FullName, ".tools", "mssql", executable);
                if (File.Exists(path)) return path;
            }
            return executable;
        }
    }

    public async Task<SqlServerConnection?> OpenAsync(CancellationToken cancellationToken, string? repositoryUrl = null)
    {
        SqlServerSettings settings = SqlServerSettings.Read(configuration);
        if (!settings.Enabled) return null;
        string connectionString = configuration["SqlServer:ConnectionString"] ?? "";
        string password = configuration["SqlServer:Password"] ?? "";
        string? database = repositoryUrl is null ? null : DeveloperSettingsStore.RepositoryDatabase(configuration, repositoryUrl);
        SqlServerSettings.Validate(settings, connectionString, password, database);
        connectionString = SqlServerSettings.BuildConnectionString(settings, connectionString, password, database);
        var variables = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is string dotnetRoot)
            variables["DOTNET_ROOT"] = dotnetRoot;
        variables["DOTNET_ENVIRONMENT"] = "Production";
        variables["MCPMSSQL_CONNECTION_STRING"] = connectionString;
        variables["MCPMSSQL_ALLOW_WRITE"] = "false";
        variables["MCPMSSQL_QUERY_MAX_ROWS"] = settings.MaxRows.ToString(CultureInfo.InvariantCulture);
        variables["MCPMSSQL_QUERY_COMMAND_TIMEOUT_SECONDS"] = settings.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        variables["MCPMSSQL_ANALYZE_COMMAND_TIMEOUT_SECONDS"] = settings.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        StdioClientTransport transport = new(new()
        {
            Name = "Alyio SQL Server", Command = Command,
            WorkingDirectory = environment.ContentRootPath,
            InheritEnvironmentVariables = false, EnvironmentVariables = variables,
            StandardErrorLines = _ => { }, // Never forward server diagnostics containing credentials.
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            McpClient client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            return new SqlServerConnection(client, settings.CommandTimeoutSeconds, settings.MaxRows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("SQL Server MCP could not start. Run CSharp/setup-sql-server.ps1, then check the SQL Server settings and .NET 10 runtime.");
        }
    }
}

public sealed class SqlServerConnection(McpClient client, int timeoutSeconds, int maxRows) : ISqlServerTools, IAsyncDisposable
{
    public async Task<string> ExecuteAsync(SqlServerRequest request, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?> { ["profile"] = "default" };
        string tool;
        switch (request.Operation)
        {
            case "schema":
                tool = "run_query";
                arguments["sql"] = """
                    SELECT TOP (@limit) s.name AS schema_name, o.name AS object_name, o.type_desc
                    FROM sys.objects o JOIN sys.schemas s ON o.schema_id = s.schema_id
                    WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V','P','FN','IF','TF')
                      AND (@schema IS NULL OR s.name = @schema)
                    ORDER BY s.name, o.name
                    """;
                arguments["parameters"] = new Dictionary<string, object?> { ["schema"] = request.Schema, ["limit"] = maxRows };
                break;
            case "migrations":
                tool = "run_query";
                string schema = QuoteIdentifier(request.Schema ?? "dbo");
                string table = QuoteIdentifier(request.Name ?? "__EFMigrationsHistory");
                // Ordering the first column works for EF Core and common custom history tables.
                arguments["sql"] = $"SELECT TOP ({maxRows}) * FROM {schema}.{table} ORDER BY 1 DESC";
                break;
            case "object":
                tool = "get_object";
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
                if (request.Kind is not ("Relation" or "Routine")) throw new ArgumentException("Choose Relation or Routine.");
                arguments["name"] = request.Name;
                if (!string.IsNullOrWhiteSpace(request.Schema)) arguments["schema"] = request.Schema;
                arguments["kind"] = request.Kind;
                arguments["includes"] = request.Kind == "Routine" ? new[] { "Definition" }
                    : new[] { "Columns", "Indexes", "Constraints", "Relationships" };
                break;
            case "query":
            case "analyze":
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);
                if (request.Sql.Length > 65536) throw new ArgumentException("SQL must be at most 64 KB.");
                tool = request.Operation == "query" ? "run_query" : "analyze_query";
                arguments["sql"] = request.Sql;
                if (request.Parameters is not null) arguments["parameters"] = request.Parameters;
                if (tool == "analyze_query") arguments["estimated"] = request.Estimated;
                // No snapshots: the workbench and agents only request bounded inline data.
                break;
            default:
                throw new ArgumentException("Unknown SQL Server operation.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 10));
        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("SQL Server operation timed out. Check that the server is reachable, or increase the SQL Server timeout in Settings.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(DescribeFailure(error.Message));
        }
        if (result.IsError == true)
            throw new InvalidOperationException(DescribeFailure(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text))));
        // Serialize protocol content rather than transport state or connection configuration.
        if (result.StructuredContent is { } structured) return JsonSerializer.Serialize(structured);
        return JsonSerializer.Serialize(new { result.Content, result.StructuredContent });
    }

    private static string DescribeFailure(string diagnostic)
    {
        // Match known causes but never return driver text, SQL, credentials, or inner exceptions.
        bool Contains(string value) => diagnostic.Contains(value, StringComparison.OrdinalIgnoreCase);
        if (Contains("certificate") || Contains("SSL Provider") || Contains("TLS handshake"))
            return "SQL Server certificate validation failed. Install a trusted server certificate, or for a known development server enable 'Trust server certificate' in Settings > SQL Server. In advanced mode, set TrustServerCertificate=True in the connection string.";
        if (Contains("Cannot open database"))
            return "SQL Server could not open the configured database. Check the database name in Settings and grant the login access to that database. Windows authentication uses the account running the API.";
        if (Contains("Login failed") || Contains("SSPI") || Contains("Kerberos"))
            return "SQL Server authentication failed. Check the authentication mode and credentials in Settings. Windows authentication uses the account running the API, which must have database access.";
        if (Contains("timeout") || Contains("timed out"))
            return "SQL Server connection or query timed out. Check server connectivity, instance/port, firewall, and the timeout in Settings.";
        if (Contains("network-related") || Contains("server was not found") || Contains("actively refused"))
            return "SQL Server is unreachable. Check the server and instance/port in Settings, SQL Server availability, and firewall access.";
        if (Contains("Invalid object name"))
            return "SQL Server object was not found. Discover its table name and schema first, then check the selected database.";
        if (Contains("permission was denied") || Contains("permission denied"))
            return "SQL Server permission was denied. The login needs read access to the requested objects.";
        return "SQL Server operation failed. Check connectivity, permissions, object names, and SELECT syntax. For migration history, discover the table and schema first.";
    }

    public static string QuoteIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("SQL Server identifiers must contain 1 to 128 characters without control characters.");
        return "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
