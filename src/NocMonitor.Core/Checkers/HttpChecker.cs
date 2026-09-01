using System.Diagnostics;

namespace NocMonitor.Core.Checkers;

/// <summary>
/// HTTP check: success = 2xx response. Unlike ping, this does evaluate
/// application health (a 5xx counts as down), not just reachability.
/// </summary>
public sealed class HttpChecker(IHttpClientFactory httpClientFactory) : IChecker
{
    public async Task<CheckOutcome> CheckAsync(CheckTarget target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target.HttpUrl))
            return CheckOutcome.Fail("No URL configured");

        var client = httpClientFactory.CreateClient(nameof(HttpChecker));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(target.TimeoutMs));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(target.HttpUrl, timeoutCts.Token);
            stopwatch.Stop();

            return response.IsSuccessStatusCode
                ? CheckOutcome.Ok((int)stopwatch.ElapsedMilliseconds)
                : CheckOutcome.Fail($"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckOutcome.Fail($"Timeout ({target.TimeoutMs}ms)");
        }
        catch (HttpRequestException ex)
        {
            return CheckOutcome.Fail(ex.Message);
        }
        // Not just HttpRequestException: HostFormPage only validates that
        // HttpUrl is non-empty, not that it's a well-formed URI, and
        // GetAsync(string) can throw other exception types (e.g.
        // UriFormatException/InvalidOperationException) for a malformed one.
        // Same class of bug PingChecker had - an uncaught exception here
        // would escape past this method entirely and only get caught by
        // CheckSchedulerService's much broader outer catch, which drops the
        // check silently (no CheckResult persisted) instead of recording a
        // clean Down, so the host would never show Down and never trigger
        // an incident/alert, it would just go quiet.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CheckOutcome.Fail(ex.Message);
        }
    }
}
