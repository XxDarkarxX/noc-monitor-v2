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
    }
}
