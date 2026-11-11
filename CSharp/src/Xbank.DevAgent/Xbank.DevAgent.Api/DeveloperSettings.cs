using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace Xbank.DevAgent.Api;

public sealed record DeveloperRepository(string Key, string RepoPath, string GitLabProjectPath,
    string? Token = null, bool HasToken = false, string Database = "", string DefaultBaseBranch = "");
public sealed record DeveloperSettings(string GitLabHost, string JiraBaseUrl, string JiraUser,
    string JiraAuthMode, DeveloperRepository[] Repositories,
    string? GitLabToken = null, string? JiraToken = null,
    bool HasGitLabToken = false, bool HasJiraToken = false,
    SqlServerSettings? SqlServer = null);

// A local installation has one OS user, rather than authenticated web accounts.
// The encrypted profile is deliberately kept outside the checkout.
public sealed class DeveloperSettingsStore(string path, IDataProtector protector) : ConfigurationProvider, IConfigurationSource
{
    private readonly object _gate = new();
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public override void Load()
    {
        if (File.Exists(path))
            Data = new Dictionary<string, string?>(JsonSerializer.Deserialize<Dictionary<string, string?>>(
                protector.Unprotect(File.ReadAllText(path)))
                ?? throw new InvalidOperationException("The developer settings file is invalid."), StringComparer.OrdinalIgnoreCase);
    }

    public static IEnumerable<IConfigurationSection> Projects(IConfiguration configuration) =>
        configuration.GetSection(configuration["DeveloperSettings:Saved"] == "true"
            ? "DeveloperRepositories" : "Projects").GetChildren();

    private static IConfigurationSection? Repository(IConfiguration configuration, string repositoryUrl)
    {
        string Normalize(string value)
        {
            string normalized = value.Trim().TrimEnd('/');
            return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? normalized[..^4] : normalized;
        }
        return Projects(configuration).FirstOrDefault(p =>
            string.Equals(Normalize(p["GitLabProjectPath"] ?? ""), Normalize(repositoryUrl), StringComparison.OrdinalIgnoreCase));
    }

    public static string? RepositoryDatabase(IConfiguration configuration, string repositoryUrl) =>
        Repository(configuration, repositoryUrl)?["Database"]?.Trim();

    public static string? GitLabToken(IConfiguration configuration, string repositoryUrl)
    {
        IConfigurationSection? project = Repository(configuration, repositoryUrl);
        return string.IsNullOrWhiteSpace(project?["Token"]) ? configuration["GitLab:Token"] : project["Token"];
    }

    public DeveloperSettings Read(IConfiguration configuration) => new(
        configuration["GitLab:Host"] ?? "", configuration["Jira:BaseUrl"] ?? "",
        configuration["Jira:User"] ?? "",
        configuration["Jira:AuthMode"] ?? "Bearer",
        Projects(configuration).Select(p => new DeveloperRepository(p["Key"] ?? p.Key,
            p["RepoPath"] ?? "", p["GitLabProjectPath"] ?? "", HasToken: !string.IsNullOrWhiteSpace(p["Token"]),
            Database: p["Database"] ?? "", DefaultBaseBranch: p["DefaultBaseBranch"] ?? "")).ToArray(),
        HasGitLabToken: !string.IsNullOrWhiteSpace(configuration["GitLab:Token"]),
        HasJiraToken: !string.IsNullOrWhiteSpace(configuration["Jira:Password"]),
        SqlServer: SqlServerSettings.Read(configuration));

    public void Save(DeveloperSettings settings, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(settings.Repositories);
        ArgumentNullException.ThrowIfNull(settings.GitLabHost);
        ArgumentNullException.ThrowIfNull(settings.JiraBaseUrl);
        ArgumentNullException.ThrowIfNull(settings.JiraUser);
        if (settings.JiraAuthMode == "Basic" && string.IsNullOrWhiteSpace(settings.JiraUser))
            throw new ArgumentException("Jira username is required for Basic authentication.");
        if (settings.Repositories.Length > 200) throw new ArgumentException("At most 200 repositories are supported.");
        if (!string.IsNullOrWhiteSpace(settings.GitLabHost) &&
            Uri.CheckHostName(settings.GitLabHost.Trim()) == UriHostNameType.Unknown)
            throw new ArgumentException("GitLab host must be a hostname without a scheme or path.");
        ValidateUrl(settings.JiraBaseUrl, "Jira URL", optional: true);
        if (settings.JiraAuthMode is not ("Basic" or "Bearer")) throw new ArgumentException("Choose Basic or Bearer Jira authentication.");
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeveloperRepository repository in settings.Repositories)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(repository.RepoPath);
            if (repository.DefaultBaseBranch is null || repository.DefaultBaseBranch.Any(char.IsControl))
                throw new ArgumentException("Default base branch must not contain control characters.");
            if (repository.Database is null || repository.Database.Trim().Length > 128 || repository.Database.Any(char.IsControl))
                throw new ArgumentException("Repository database must be at most 128 characters without control characters.");
            if (string.IsNullOrWhiteSpace(repository.Key) || !keys.Add(repository.Key.Trim()))
                throw new ArgumentException("Repository names must be nonempty and unique.");
            ValidateUrl(repository.GitLabProjectPath, "Repository URL");
            if (!string.IsNullOrWhiteSpace(repository.RepoPath) && !Path.IsPathFullyQualified(repository.RepoPath))
                throw new ArgumentException("Local repository paths must be absolute.");
        }
        lock (_gate)
        {
            SqlServerSettings sqlServer = settings.SqlServer ?? SqlServerSettings.Read(configuration);
            string connectionString = sqlServer.ConnectionString ?? configuration["SqlServer:ConnectionString"] ?? "";
            string password = sqlServer.Password ?? configuration["SqlServer:Password"] ?? "";
            SqlServerSettings.Validate(sqlServer, connectionString, password);
            Dictionary<string, string?> next = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DeveloperSettings:Saved"] = "true",
                ["GitLab:Host"] = settings.GitLabHost.Trim(),
                ["GitLab:Token"] = settings.GitLabToken?.Trim() ?? configuration["GitLab:Token"] ?? "",
                ["Jira:BaseUrl"] = settings.JiraBaseUrl.Trim().TrimEnd('/'),
                ["Jira:User"] = settings.JiraUser.Trim(),
                ["Jira:AuthMode"] = settings.JiraAuthMode,
                ["Jira:Password"] = settings.JiraToken?.Trim() ?? configuration["Jira:Password"] ?? "",
                ["SqlServer:Enabled"] = sqlServer.Enabled.ToString(),
                ["SqlServer:ConnectionString"] = connectionString,
                ["SqlServer:Username"] = sqlServer.Username.Trim(),
                ["SqlServer:Password"] = password,
                ["SqlServer:Server"] = sqlServer.Server.Trim(),
                ["SqlServer:Database"] = sqlServer.Database.Trim(),
                ["SqlServer:UseWindowsAuthentication"] = sqlServer.UseWindowsAuthentication.ToString(),
                ["SqlServer:UseConnectionString"] = sqlServer.UseConnectionString.ToString(),
                ["SqlServer:TrustServerCertificate"] = sqlServer.TrustServerCertificate.ToString(),
                ["SqlServer:MaxRows"] = sqlServer.MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SqlServer:CommandTimeoutSeconds"] = sqlServer.CommandTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            int index = 0;
            foreach (DeveloperRepository repository in settings.Repositories)
            {
                string prefix = $"DeveloperRepositories:{index++}:";
                next[prefix + "Key"] = repository.Key.Trim();
                next[prefix + "RepoPath"] = repository.RepoPath.Trim();
                next[prefix + "GitLabProjectPath"] = repository.GitLabProjectPath.Trim();
                next[prefix + "Database"] = repository.Database.Trim();
                next[prefix + "DefaultBaseBranch"] = repository.DefaultBaseBranch.Trim();
                IConfigurationSection? previous = Projects(configuration).FirstOrDefault(p =>
                    string.Equals(p["Key"] ?? p.Key, repository.Key.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p["GitLabProjectPath"], repository.GitLabProjectPath.Trim(), StringComparison.OrdinalIgnoreCase));
                next[prefix + "Token"] = repository.Token?.Trim() ?? previous?["Token"] ?? "";
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, protector.Protect(JsonSerializer.Serialize(next)));
            File.Move(temporary, path, overwrite: true);
            Data = next;
            OnReload();
        }
    }

    private static void ValidateUrl(string value, string label, bool optional = false)
    {
        if (optional && string.IsNullOrWhiteSpace(value)) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException($"{label} must be an HTTPS URL without credentials, query, or fragment.");
    }
}
