using System.Net;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.Configuration;

namespace Xbank.DevAgent.Tests;

public sealed class RunPreflightTests
{
    [Theory]
    [InlineData("planner")]
    [InlineData("developer")]
    [InlineData("reviewer")]
    [InlineData("release_manager")]
    [InlineData("jira")]
    [InlineData("security")]
    public async Task MissingKeyIsRejectedBeforeNetworkOrSessionCreation(string agent)
    {
        using ProbeHandler handler = new();
        using HttpClient client = new(handler);
        RepositorySessionService service = Service(client);
        var agents = Agents();
        agents[agent] = new(Provider(), "  ");

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.PreflightAsync(agents));
        Assert.Contains(agent, error.Message);
        Assert.Contains("test-model", error.Message);
        Assert.Contains("API key", error.Message);
        Assert.Contains("connection settings", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Request(agents)));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("DeepSeek", "https://api.deepseek.com")]
    [InlineData("AIFA", "https://aifa-chatbot.dev.dotin.ir")]
    public async Task ServerConfiguredKeysAreAccepted(string provider, string endpoint)
    {
        using ProbeHandler handler = new();
        using HttpClient client = new(handler);
        var agents = Agents();
        agents[AgentNames.Planner] = new(Provider() with { Url = endpoint }, "");
        await Service(client, new Dictionary<string, string?> { [$"Providers:{provider}:ApiKey"] = "server-key" })
            .PreflightAsync(agents);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(405)]
    [InlineData(503)]
    public async Task AnyHttpResponseProvesConnectivityWithoutSendingCredentials(int status)
    {
        using ProbeHandler handler = new() { Status = (HttpStatusCode)status };
        using HttpClient client = new(handler);
        await Service(client).PreflightAsync(Agents());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineOrTimeoutStopsBeforeSessionIsStored(bool timeout)
    {
        using ProbeHandler handler = new() { Failure = timeout ? new TaskCanceledException() : new HttpRequestException("offline") };
        using HttpClient client = new(handler);
        RepositorySessionService service = Service(client);
        var error = await Assert.ThrowsAsync<RunConnectivityException>(() => service.CreateAsync(Request(Agents())));
        Assert.Contains("internet connection", error.Message);
        Assert.Contains("example.test", error.Message);
        Assert.Contains("try again", error.Message);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using ProbeHandler handler = new();
        using HttpClient client = new(handler);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RunConnectivityCheck(client).CheckAsync([Provider().Url], cancellation.Token));
    }

    private static RepositorySessionService Service(HttpClient client, Dictionary<string, string?>? config = null) =>
        new(null!, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build(), new RunConnectivityCheck(client));

    private static Dictionary<string, AgentModelConfiguration> Agents() => AgentNames.Required
        .ToDictionary(name => name, _ => new AgentModelConfiguration(Provider(), "test-key"));

    private static ModelProviderDefinition Provider() => new("test-model", "Test", "https://example.test/v1",
        true, false, 8192, 2048, false, null, []);

    private static CreateSessionRequest Request(Dictionary<string, AgentModelConfiguration> agents) =>
        new("https://github.com/example/repository", "main", "WEPOD-42 Update behavior", agents, null,
            SourceControl: "github", CodeGraphEnabled: false);

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public Exception? Failure { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Null(request.Content);
            Assert.Empty(request.Headers);
            return Failure is not null ? Task.FromException<HttpResponseMessage>(Failure)
                : Task.FromResult(new HttpResponseMessage(Status));
        }
    }
}
