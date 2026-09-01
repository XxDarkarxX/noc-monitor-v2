using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

namespace NocMonitor.Core.Checkers;

/// <summary>ICMP check: basic reachability, without evaluating application health.</summary>
public sealed class PingChecker(ILogger<PingChecker> logger) : IChecker
{
    private static readonly byte[] Buffer = new byte[32];

    public async Task<CheckOutcome> CheckAsync(CheckTarget target, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(target.Ip, out var address))
            return CheckOutcome.Fail("Invalid or unconfigured IP");

        using var ping = new Ping();
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await ping.SendPingAsync(
                address, TimeSpan.FromMilliseconds(target.TimeoutMs), Buffer, options: null, cancellationToken);

            // [DIAG-PING] temporary - investigating why SendPingAsync fails
            // in production against LAN IPs that a native `ping` reaches
            // fine (both from the container and the host) - see the
            // exception branch below for the failing case; this branch logs
            // even a "successful" call (Status might still not be Success)
            // so we can see the actual elapsed time against target.TimeoutMs
            // ({target.TimeoutMs}ms, 1000 by default) - if .NET is falling
            // back to shelling out to the `ping` binary per the investigation
            // in progress, process-spawn overhead alone could plausibly blow
            // through a 1s timeout under load even though the ping itself
            // would succeed. Remove once we've confirmed the actual cause.
            logger.LogWarning(
                "[DIAG-PING] {Ip}: SendPingAsync returned in {ElapsedMs}ms (timeout={TimeoutMs}ms) - Status={Status} RoundtripTime={RoundtripMs}ms",
                target.Ip, sw.ElapsedMilliseconds, target.TimeoutMs, reply.Status, reply.RoundtripTime);

            return reply.Status == IPStatus.Success
                ? CheckOutcome.Ok((int)reply.RoundtripTime)
                : CheckOutcome.Fail(reply.Status.ToString());
        }
        catch (PingException ex)
        {
            LogDiagException(target.Ip, target.TimeoutMs, sw.ElapsedMilliseconds, ex);
            return CheckOutcome.Fail(ex.InnerException?.Message ?? ex.Message);
        }
        // Not just PingException: on Linux, Ping.SendPingAsync can throw
        // PlatformNotSupportedException directly (unwrapped) when it can't
        // create a raw ICMP socket AND can't find the OS `ping` binary to
        // fall back to - e.g. a minimal container image missing that binary.
        // Left uncaught here, that escaped past this method entirely and was
        // only caught by CheckSchedulerService's much broader outer catch,
        // which drops the check silently (no CheckResult persisted at all)
        // instead of recording a clean Down - so the host never showed as
        // Down and never triggered an incident/alert, it just went quiet.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDiagException(target.Ip, target.TimeoutMs, sw.ElapsedMilliseconds, ex);
            return CheckOutcome.Fail(ex.Message);
        }
    }

    // [DIAG-PING] temporary - logs the full exception chain (type + message
    // per level, since PingException wraps an inner one) plus the stack
    // trace, so we can tell apart the raw-socket path, the unprivileged
    // ping_group_range path, and the subprocess-`ping` fallback .NET tries
    // in order on Linux - each fails with a different exception shape.
    // Remove this whole method (and its two call sites above) once the
    // production logs confirm which one this actually is.
    private void LogDiagException(string ip, int timeoutMs, long elapsedMs, Exception ex)
    {
        var chain = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            chain.Add($"{e.GetType().FullName}: {e.Message}");

        logger.LogWarning(
            "[DIAG-PING] {Ip}: threw after {ElapsedMs}ms (timeout={TimeoutMs}ms) - chain: {Chain} | StackTrace: {StackTrace}",
            ip, elapsedMs, timeoutMs, string.Join(" -> ", chain), ex.StackTrace);
    }
}
