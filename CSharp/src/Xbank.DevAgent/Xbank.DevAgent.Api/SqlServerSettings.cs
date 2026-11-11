using Microsoft.Data.SqlClient;

namespace Xbank.DevAgent.Api;

public sealed record SqlServerSettings(bool Enabled = false, string? ConnectionString = null,
    string Username = "", string? Password = null, int MaxRows = 200,
    int CommandTimeoutSeconds = 30, bool HasConnectionString = false, bool HasPassword = false,
    string Server = "", string Database = "", bool UseWindowsAuthentication = true, bool UseConnectionString = true,
    bool TrustServerCertificate = false)
{
    public static SqlServerSettings Read(IConfiguration config) => new(
        config.GetValue("SqlServer:Enabled", false), Username: config["SqlServer:Username"] ?? "",
        MaxRows: config.GetValue("SqlServer:MaxRows", 200),
        CommandTimeoutSeconds: config.GetValue("SqlServer:CommandTimeoutSeconds", 30),
        HasConnectionString: !string.IsNullOrWhiteSpace(config["SqlServer:ConnectionString"]),
        HasPassword: !string.IsNullOrEmpty(config["SqlServer:Password"]),
        Server: config["SqlServer:Server"] ?? "", Database: config["SqlServer:Database"] ?? "",
        UseWindowsAuthentication: config.GetValue("SqlServer:UseWindowsAuthentication", true),
        UseConnectionString: config.GetValue("SqlServer:UseConnectionString",
            !string.IsNullOrWhiteSpace(config["SqlServer:ConnectionString"])),
        TrustServerCertificate: config.GetValue("SqlServer:TrustServerCertificate", false));

    public static void Validate(SqlServerSettings settings, string connectionString, string password, string? databaseOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(databaseOverride)) settings = settings with { Database = databaseOverride.Trim() };
        if (settings.Username is null) throw new ArgumentException("SQL Server username cannot be null.");
        if (settings.MaxRows is < 1 or > 1000) throw new ArgumentException("SQL Server row limit must be between 1 and 1000.");
        if (settings.CommandTimeoutSeconds is < 1 or > 300) throw new ArgumentException("SQL Server timeout must be between 1 and 300 seconds.");
        if (settings.Server is null || settings.Database is null)
            throw new ArgumentException("SQL Server server and database cannot be null.");
        if (!settings.UseConnectionString)
        {
            if (string.IsNullOrWhiteSpace(settings.Server) && string.IsNullOrWhiteSpace(settings.Database) && !settings.Enabled) return;
            if (string.IsNullOrWhiteSpace(settings.Server) || string.IsNullOrWhiteSpace(settings.Database))
                throw new ArgumentException("Enter a SQL Server name and database.");
            if (!settings.UseWindowsAuthentication && string.IsNullOrWhiteSpace(settings.Username))
                throw new ArgumentException("Enter a SQL username or select Windows authentication.");
        }
        else if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (settings.Enabled) throw new ArgumentException("Save a SQL Server connection string before enabling access.");
            return;
        }
        _ = BuildConnectionString(settings, connectionString, password, databaseOverride);
    }

    public static string BuildConnectionString(SqlServerSettings settings, string connectionString, string password,
        string? databaseOverride = null)
    {
        try
        {
            SqlConnectionStringBuilder builder = settings.UseConnectionString ? new(connectionString) : new()
            {
                DataSource = settings.Server.Trim(), InitialCatalog = settings.Database.Trim(),
                IntegratedSecurity = settings.UseWindowsAuthentication, Encrypt = true,
                TrustServerCertificate = settings.TrustServerCertificate,
            };
            if (!string.IsNullOrWhiteSpace(databaseOverride)) builder.InitialCatalog = databaseOverride.Trim();
            if (string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog))
                throw new ArgumentException();
            if (!settings.UseConnectionString && settings.UseWindowsAuthentication)
            {
                builder.Remove("User ID");
                builder.Remove("Password");
            }
            else if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                builder.IntegratedSecurity = false;
                builder.UserID = settings.Username.Trim();
                builder.Password = password;
            }
            else if (!string.IsNullOrEmpty(password)) builder.Password = password;
            builder.PersistSecurityInfo = false;
            builder.ConnectTimeout = Math.Min(settings.CommandTimeoutSeconds, 30);
            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            // SqlClient errors can echo the original connection string.
            throw new ArgumentException("SQL Server connection string is invalid. Include Server and Database, and a supported authentication mode.");
        }
    }
}
