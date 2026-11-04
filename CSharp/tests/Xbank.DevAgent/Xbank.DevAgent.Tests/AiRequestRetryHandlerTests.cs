using System.Net;
using System.Text;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class AiRequestRetryHandlerTests
{
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task RetriesExactlyThreeTimesWithExponentialDelays(int status)
    {
        List<TimeSpan> delays = [];
        List<TrackedContent> bodies = [];
        RecordingHandler server = new((_, _) =>
        {
            TrackedContent body = new(); bodies.Add(body);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = body });
        });
        using HttpClient client = new(new AiRequestRetryHandler(server, TimeSpan.FromMilliseconds(1), delays.Add));

        using HttpResponseMessage response = await client.GetAsync("https://provider.test/chat");

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(4, server.Calls);
        Assert.Equal(new[] { 1d, 2d, 4d }, delays.Select(delay => delay.TotalMilliseconds));
        Assert.All(bodies.Take(3), body => Assert.True(body.Disposed));
        Assert.False(bodies[^1].Disposed);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task SuccessAndPermanentErrorsAreNotRetried(int status)
    {
        RecordingHandler server = new((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using HttpClient client = new(new AiRequestRetryHandler(server, TimeSpan.FromMilliseconds(1)));
        using HttpResponseMessage response = await client.GetAsync("https://provider.test/chat");

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task InvalidCredentialIsActionableWithoutRetryingOrLeakingResponse()
    {
        TrackedContent body = new();
        RecordingHandler server = new((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = body }));
        using HttpClient client = new(new AiRequestRetryHandler(server));

        ConfigurationException error = await Assert.ThrowsAsync<ConfigurationException>(
            () => client.GetAsync("https://provider.test/chat?token=secret"));

        Assert.Contains("provider.test", error.Message);
        Assert.Contains("HTTP 401", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("Error response", error.Message);
        Assert.Equal(1, server.Calls);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task StreamingClientPropagatesCredentialFailureAsConfigurationError()
    {
        RecordingHandler server = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using Microsoft.Extensions.AI.IChatClient client = ModelChatClientFactory.Create(
            new WorkflowSettings("https://provider.test/v1", "test-model", "invalid-token"), server);

        await Assert.ThrowsAsync<ConfigurationException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")])) { }
        });
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task RecoversFromNetworkAndServerFailuresWithoutLosingThePostBody()
    {
        List<string> payloads = [];
        int attempt = 0;
        RecordingHandler server = new(async (request, cancellationToken) =>
        {
            using MemoryStream stream = new();
            await request.Content!.CopyToAsync(stream, cancellationToken);
            payloads.Add(Encoding.UTF8.GetString(stream.ToArray()));
            if (++attempt == 1) throw new HttpRequestException("Connection reset");
            return new HttpResponseMessage(attempt == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        });
        using HttpClient client = new(new AiRequestRetryHandler(server, TimeSpan.FromMilliseconds(1)));
        using OneShotContent content = new();
        using HttpResponseMessage response = await client.PostAsync("https://provider.test/chat", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, server.Calls);
        Assert.All(payloads, payload => Assert.Equal("{\"messages\":[\"hello\"]}", payload));
        Assert.Equal(1, content.Serializations);
    }

    [Fact]
    public async Task ExhaustedNetworkFailuresPropagateTheOriginalException()
    {
        HttpRequestException failure = new("Connection reset");
        RecordingHandler server = new((_, _) => throw failure);
        using HttpClient client = new(new AiRequestRetryHandler(server, TimeSpan.FromMilliseconds(1)));

        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://provider.test/chat")));
        Assert.Equal(4, server.Calls);
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsFurtherAttempts()
    {
        using CancellationTokenSource cancellation = new();
        RecordingHandler server = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using HttpClient client = new(new AiRequestRetryHandler(server, onRetry: _ => cancellation.Cancel()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://provider.test/chat", cancellation.Token));
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task AnInterruptedResponseBodyIsNotReplayed()
    {
        RecordingHandler server = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenBody() }));
        using HttpClient client = new(new AiRequestRetryHandler(server, TimeSpan.FromMilliseconds(1)));
        using HttpResponseMessage response = await client.GetAsync("https://provider.test/chat", HttpCompletionOption.ResponseHeadersRead);

        await Assert.ThrowsAsync<HttpRequestException>(() => response.Content.ReadAsStringAsync());
        Assert.Equal(1, server.Calls);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }

    private sealed class TrackedContent() : StringContent("Error response")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class OneShotContent : HttpContent
    {
        public int Serializations { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            if (++Serializations > 1) throw new InvalidOperationException("The request stream cannot be replayed.");
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"messages\":[\"hello\"]}"));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class BrokenBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new HttpRequestException("Response stream interrupted"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
