using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maf.Handoff.Core;

namespace Maf.Handoff.Api;

public sealed record JiraCreateIssueRequest(
    string ProjectKey,
    string IssueType,
    string Summary,
    string? Description,
    JsonElement? AdditionalFields = null);

// Adapted from ReleaseManager/Services/JiraService.cs. The workflow agent receives
// only read tools; create and comment are available through explicit workbench actions.
public sealed partial class JiraService(HttpClient httpClient, IConfiguration configuration) : IJiraIssueSearch
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IConfiguration _configuration = configuration;

    public async Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(
        string jql,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jql);
        if (jql.Length > 2000)
        {
            throw new ArgumentException("JQL must be 2000 characters or fewer.", nameof(jql));
        }

        maxResults = Math.Clamp(maxResults, 1, 100);
        string path = $"rest/api/2/search?jql={Uri.EscapeDataString(jql)}&maxResults={maxResults}&fields=summary,status,assignee,created,updated";
        using JsonDocument document = await GetJsonAsync(path, cancellationToken);
        if (!document.RootElement.TryGetProperty("issues", out JsonElement issues) ||
            issues.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<JiraIssueInfo> results = [];
        foreach (JsonElement issue in issues.EnumerateArray())
        {
            JsonElement fields = issue.TryGetProperty("fields", out JsonElement value) ? value : default;
            results.Add(new JiraIssueInfo(
                ReadString(issue, "key"),
                ReadString(fields, "summary"),
                ReadNestedString(fields, "status", "name"),
                ReadNestedString(fields, "assignee", "displayName", "Unassigned"),
                ReadString(fields, "created"),
                ReadString(fields, "updated")));
        }

        return results;
    }

    public async Task<string?> GetIssueTitleByKeyAsync(
        string issueKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(issueKey);

        using JsonDocument document = await GetJsonAsync(
            $"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}?fields=summary",
            cancellationToken);
        return document.RootElement.TryGetProperty("fields", out JsonElement fields)
            ? ReadString(fields, "summary")
            : null;
    }

    // These search modes follow the referenced service's JQL behavior.
    public Task<IReadOnlyList<JiraIssueInfo>> GetTasksByProjectAsync(
        string projectKey,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(projectKey, @"^[A-Za-z][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException("Invalid Jira project key.", nameof(projectKey));
        }

        return SearchIssuesAsync($"project = \"{projectKey}\" ORDER BY updated DESC", maxResults, cancellationToken);
    }

    public Task<IReadOnlyList<JiraIssueInfo>> GetTasksByTextSearchAsync(
        string searchTerm,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchTerm);
        string escaped = searchTerm.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return SearchIssuesAsync(
            $"(summary ~ \"{escaped}\" OR description ~ \"{escaped}\") ORDER BY updated DESC",
            maxResults,
            cancellationToken);
    }

    public async Task<JsonElement> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await GetJsonAsync("rest/api/2/myself", cancellationToken);
        return document.RootElement.Clone();
    }

    public async Task<JsonElement> GetIssueDetailsAsync(
        string issueKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(issueKey);
        using JsonDocument document = await GetJsonAsync(
            $"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}?fields=summary,description,status,assignee,created,updated,comment,attachment",
            cancellationToken);
        return document.RootElement.Clone();
    }

    public async Task<string> CreateIssueAsync(JiraCreateIssueRequest input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.ProjectKey) ||
            !System.Text.RegularExpressions.Regex.IsMatch(input.ProjectKey, @"^[A-Za-z][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException("A valid Jira project key is required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(input.IssueType);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Summary);
        if (input.Summary.Length > 255)
        {
            throw new ArgumentException("Issue summary must be 255 characters or fewer.");
        }

        JsonObject fields = new()
        {
            ["project"] = new JsonObject { ["key"] = input.ProjectKey },
            ["issuetype"] = new JsonObject { ["name"] = input.IssueType },
            ["summary"] = input.Summary,
            ["description"] = input.Description ?? string.Empty,
        };
        if (input.AdditionalFields is JsonElement additional)
        {
            if (additional.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Additional fields must be a JSON object.");
            }
            foreach (JsonProperty field in additional.EnumerateObject())
            {
                if (fields.ContainsKey(field.Name))
                {
                    throw new ArgumentException($"Additional fields cannot replace '{field.Name}'.");
                }
                fields[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            }
        }
        if (input.ProjectKey.Equals("OPIM", StringComparison.OrdinalIgnoreCase) &&
            !fields.ContainsKey("customfield_14836") &&
            !string.IsNullOrWhiteSpace(_configuration["Jira:Requester"]))
        {
            fields["customfield_14836"] = _configuration["Jira:Requester"];
        }

        using JsonDocument response = await PostJsonAsync("rest/api/2/issue", new JsonObject { ["fields"] = fields }, cancellationToken);
        string key = ReadString(response.RootElement, "key");
        return string.IsNullOrWhiteSpace(key)
            ? throw new HttpRequestException("Jira did not return a new issue key.")
            : key;
    }

    public async Task<JsonElement> PostCommentAsync(
        string issueKey,
        string comment,
        CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(issueKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        using JsonDocument response = await PostJsonAsync(
            $"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}/comment",
            new JsonObject { ["body"] = comment },
            cancellationToken);
        return response.RootElement.Clone();
    }

    private static void ValidateIssueKey(string issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey) ||
            !System.Text.RegularExpressions.Regex.IsMatch(issueKey, @"^[A-Za-z][A-Za-z0-9_]*-\d+$"))
        {
            throw new ArgumentException("A full Jira issue key is required.", nameof(issueKey));
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        Uri baseUrl = GetBaseUrl();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseUrl, path));
        AddAuthentication(request);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private async Task<JsonDocument> PostJsonAsync(string path, JsonNode body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(GetBaseUrl(), path));
        AddAuthentication(request);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private void AddAuthentication(HttpRequestMessage request)
    {
        string user = _configuration["Jira:User"] ?? throw new InvalidOperationException("Jira:User is required.");
        string password = _configuration["Jira:Password"] ?? throw new InvalidOperationException("Jira:Password is required.");
        string authMode = _configuration["Jira:AuthMode"] ?? "Basic";
        request.Headers.Authorization = authMode.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            ? new AuthenticationHeaderValue("Bearer", password)
            : authMode.Equals("Basic", StringComparison.OrdinalIgnoreCase)
                ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")))
                : throw new InvalidOperationException("Jira:AuthMode must be Basic or Bearer.");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            string explanation = string.Empty;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    await using Stream errorStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using JsonDocument error = await JsonDocument.ParseAsync(errorStream, cancellationToken: cancellationToken);
                    List<string> messages = [];
                    if (error.RootElement.ValueKind == JsonValueKind.Object &&
                        error.RootElement.TryGetProperty("errorMessages", out JsonElement general) &&
                        general.ValueKind == JsonValueKind.Array)
                    {
                        messages.AddRange(general.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString() ?? string.Empty));
                    }
                    if (error.RootElement.ValueKind == JsonValueKind.Object &&
                        error.RootElement.TryGetProperty("errors", out JsonElement fields) &&
                        fields.ValueKind == JsonValueKind.Object)
                    {
                        messages.AddRange(fields.EnumerateObject()
                            .Select(field => $"{field.Name}: {field.Value}"));
                    }
                    explanation = string.Join("; ", messages);
                    if (explanation.Length > 800) explanation = explanation[..800] + "…";
                }
                catch (JsonException)
                {
                    // Some Jira gateways return a non-JSON error despite the content type.
                }
            }
            throw new HttpRequestException(
                $"Jira returned HTTP {(int)response.StatusCode}." +
                (string.IsNullOrWhiteSpace(explanation) ? string.Empty : $" {explanation}"),
                null,
                response.StatusCode);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private Uri GetBaseUrl()
    {
        string configured = _configuration["Jira:BaseUrl"] ?? throw new InvalidOperationException("Jira:BaseUrl is required.");
        if (!Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Jira:BaseUrl must be an HTTPS URL without embedded credentials.");
        }

        return uri;
    }

    private static string ReadString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadNestedString(JsonElement element, string key, string nestedKey, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out JsonElement nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? ReadString(nested, nestedKey)
            : fallback;
}
