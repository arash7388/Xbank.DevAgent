using System.Net;
using Microsoft.Extensions.AI;
using Polly;
using Polly.Retry;

namespace Maf.Handoff.Core;

// Retry transport failures before a response is returned. A partially delivered stream is never replayed.
internal sealed class AiRequestRetryHandler : DelegatingHandler
{
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    internal AiRequestRetryHandler(HttpMessageHandler innerHandler, TimeSpan? initialDelay = null,
        Action<TimeSpan>? onRetry = null) : base(innerHandler)
    {
        _pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = initialDelay ?? TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>(error => error.StatusCode is null || IsTransient(error.StatusCode.Value))
                    .HandleResult(response => IsTransient(response.StatusCode)),
                OnRetry = args =>
                {
                    args.Outcome.Result?.Dispose();
                    onRetry?.Invoke(args.RetryDelay);
                    return default;
                },
            }).Build();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // SDK JSON payloads must remain replayable even when backed by a forward-only content stream.
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);

        return await _pipeline.ExecuteAsync(
            token => new ValueTask<HttpResponseMessage>(base.SendAsync(request, token)), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}

internal sealed class TransportOwnedChatClient(IChatClient inner, HttpClient transport) : DelegatingChatClient(inner)
{
    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) transport.Dispose(); }
    }
}
