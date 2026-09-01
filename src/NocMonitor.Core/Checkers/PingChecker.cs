using System.Net;
using System.Net.NetworkInformation;

namespace NocMonitor.Core.Checkers;

/// <summary>ICMP check: basic reachability, without evaluating application health.</summary>
public sealed class PingChecker : IChecker
{
    private static readonly byte[] Buffer = new byte[32];

    public async Task<CheckOutcome> CheckAsync(CheckTarget target, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(target.Ip, out var address))
            return CheckOutcome.Fail("Invalid or unconfigured IP");

        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(
                address, TimeSpan.FromMilliseconds(target.TimeoutMs), Buffer, options: null, cancellationToken);
            return reply.Status == IPStatus.Success
                ? CheckOutcome.Ok((int)reply.RoundtripTime)
                : CheckOutcome.Fail(reply.Status.ToString());
        }
        catch (PingException ex)
        {
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
            return CheckOutcome.Fail(ex.Message);
        }
    }
}
