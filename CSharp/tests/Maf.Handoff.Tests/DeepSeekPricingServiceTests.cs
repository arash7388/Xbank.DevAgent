using System.Net;
using Maf.Handoff.Api;

namespace Maf.Handoff.Tests;

public sealed class DeepSeekPricingServiceTests
{
    [Fact]
    public async Task HttpTimeoutIsReportedAsRemoteServiceFailureAndReleasesRefreshLock()
    {
        using HttpClient http = new(new WaitingHandler()) { Timeout = TimeSpan.FromMilliseconds(50) };
        DeepSeekPricingService service = new(http);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(
                () => service.GetCurrentAsync(deadline.Token));
            Assert.Contains("DeepSeek pricing request timed out", error.Message);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        }
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        WaitingHandler handler = new();
        using HttpClient http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        DeepSeekPricingService service = new(http);
        using CancellationTokenSource cancellation = new();

        Task<DeepSeekPricingSnapshot> request = service.GetCurrentAsync(cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData("2026-10-08T01:00:00Z", "peak", 0.3)]
    [InlineData("2026-10-08T04:00:00Z", "off-peak", 0.15)]
    [InlineData("2026-10-08T06:00:00Z", "peak", 0.3)]
    [InlineData("2026-10-08T10:00:00Z", "off-peak", 0.15)]
    [InlineData("2026-10-03T02:00:00Z", "off-peak", 0.15)]
    [InlineData("2026-10-05T02:00:00Z", "off-peak", 0.15)]
    public async Task SelectsOfficialRatesForUtcSchedule(string instant, string band, double input)
    {
        using HttpClient http = new(new PricingHandler());
        DeepSeekPricingService service = new(http, new FixedClock(DateTimeOffset.Parse(instant)));
        DeepSeekPricingSnapshot snapshot = await service.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(band, snapshot.PriceBand);
        DeepSeekModelPrice flash = snapshot.Models[0];
        Assert.Equal((decimal)input, flash.InputUsdPerMillion);
        Assert.Equal(flash.InputUsdPerMillion / 50m, flash.CachedInputUsdPerMillion);
        Assert.Equal(flash.InputUsdPerMillion * 4m, flash.OutputUsdPerMillion);
        Assert.Equal(band == "peak" ? 1.32m : 0.66m, snapshot.Models[1].InputUsdPerMillion);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PricingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    <table>
                    <tr><td>MODEL</td><td>deepseek-flash<sup>(1)</sup></td><td>deepseek-v4-pro</td></tr>
                    <tr><td rowspan="6">PRICING</td><td rowspan="2">1M INPUT TOKENS<br>(CACHE HIT)</td><td>OFF-PEAK</td><td>$0.003</td><td>$0.022</td></tr>
                    <tr><td>PEAK</td><td>$0.006</td><td>$0.044</td></tr>
                    <tr><td rowspan="2">1M INPUT TOKENS<br>(CACHE MISS)</td><td>OFF-PEAK</td><td>$0.15</td><td>$0.66</td></tr>
                    <tr><td>PEAK</td><td>$0.3</td><td>$1.32</td></tr>
                    <tr><td rowspan="2">1M OUTPUT TOKENS</td><td>OFF-PEAK</td><td>$0.6</td><td>$1.98</td></tr>
                    <tr><td>PEAK</td><td>$1.2</td><td>$3.96</td></tr>
                    </table>
                    """),
            });
    }
}
