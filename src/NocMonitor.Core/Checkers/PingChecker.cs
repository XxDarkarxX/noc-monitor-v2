using System.Net;
using System.Net.NetworkInformation;

namespace NocMonitor.Core.Checkers;

/// <summary>ICMP check: basic reachability, without evaluating application health.</summary>
public sealed class PingChecker : IChecker
{
    public async Task<CheckOutcome> CheckAsync(CheckTarget target, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(target.Ip, out var address))
            return CheckOutcome.Fail("Invalid or unconfigured IP");

        using var ping = new Ping();
        try
        {
            // No custom payload buffer (used to pass a 32-byte one here): on
            // Linux, when the raw ICMP socket isn't available in-process,
            // .NET falls back to shelling out to the OS `ping` binary - and
            // that fallback explicitly rejects a non-default buffer with
            // PlatformNotSupportedException("Unable to send custom ping
            // payload..."). Confirmed in production: the native `ping`
            // binary reached every LAN host fine (both from the Docker host
            // and via --entrypoint ping inside the container), but every
            // check here failed with exactly that exception - the custom
            // buffer was the actual cause, not a network or capability
            // problem. A default-payload ping is all a basic reachability
            // check needs anyway.
            var reply = await ping.SendPingAsync(
                address, TimeSpan.FromMilliseconds(target.TimeoutMs), null, options: null, cancellationToken);
            return reply.Status == IPStatus.Success
                ? CheckOutcome.Ok((int)reply.RoundtripTime)
                : CheckOutcome.Fail(reply.Status.ToString());
        }
        catch (PingException ex)
        {
            return CheckOutcome.Fail(ex.InnerException?.Message ?? ex.Message);
        }
        // Not just PingException: on Linux, Ping.SendPingAsync can throw
        // PlatformNotSupportedException directly (unwrapped) - as it did
        // here. Left uncaught, that would escape past this method entirely
        // and only get caught by CheckSchedulerService's much broader outer
        // catch, which drops the check silently (no CheckResult persisted)
        // instead of recording a clean Down - so the host would never show
        // Down and never trigger an incident/alert, it would just go quiet.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CheckOutcome.Fail(ex.Message);
        }
    }
}
