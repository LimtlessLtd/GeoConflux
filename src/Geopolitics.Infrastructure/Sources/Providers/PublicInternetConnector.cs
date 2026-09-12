using System.Net;
using System.Net.Sockets;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Opens the TCP connection behind every OSINT poll, refusing any that would land on a non-public
/// address.
/// <para>
/// This sits at the connection rather than at the request because that is the only place that sees
/// every attempt. A redirect chain, a DNS record that changes between the check and the connection,
/// and the original request all funnel through here, so one rule covers all three instead of three
/// rules that have to be kept in agreement.
/// </para>
/// </summary>
internal static class PublicInternetConnector
{
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endPoint = context.DnsEndPoint;

        // Resolves a literal address to itself, so a URL written with an IP is judged by the same
        // rule as one written with a name.
        var resolved = await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);
        var permitted = Array.FindAll(resolved, OutboundAddressPolicy.IsAllowed);

        if (permitted.Length == 0)
        {
            throw new HttpRequestException(
                $"Refusing to connect to '{endPoint.Host}': it resolves only to addresses outside the public internet. "
                + "OSINT adapters may reach public feeds only.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            // Only the permitted addresses are offered, so the happy-eyeballs fallback cannot reach a
            // rejected one after the first choice fails.
            await socket.ConnectAsync(permitted, endPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
