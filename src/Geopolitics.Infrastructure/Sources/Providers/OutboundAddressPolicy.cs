using System.Net;
using System.Net.Sockets;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Decides which network addresses an OSINT poll is allowed to reach.
/// <para>
/// These adapters exist to read public feeds, so anything that is not on the public internet is out
/// of scope for them by definition. Saying so explicitly is what closes the redirect problem: a feed
/// URL is chosen by whoever deploys this and can be trusted, but the <em>response</em> cannot, and a
/// publisher that answers with <c>302 Location: http://169.254.169.254/latest/meta-data/</c> would
/// otherwise have this process fetch a cloud instance's credentials on its behalf. The same is true
/// of a redirect to <c>localhost</c>, which would turn an outbound poll into a request against this
/// application's own API.
/// </para>
/// <para>
/// The check is made against the resolved <see cref="IPAddress"/> at connection time rather than
/// against the hostname in the URL. Filtering on hostnames catches only the naive case: a name under
/// the redirecting party's control can resolve to whatever they choose, so a host that looks external
/// and points at <c>127.0.0.1</c> passes a name check and fails this one.
/// </para>
/// </summary>
internal static class OutboundAddressPolicy
{
    /// <summary>
    /// Whether a poll may open a connection to <paramref name="address"/>.
    /// </summary>
    public static bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // An IPv6 address carrying an IPv4 one is the same destination written differently, and
        // judging it in its mapped form would let ::ffff:127.0.0.1 through a check that rejects
        // 127.0.0.1.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsAllowedV4(address),
            AddressFamily.InterNetworkV6 => IsAllowedV6(address),

            // A family these adapters have no business using at all.
            _ => false,
        };
    }

    private static bool IsAllowedV4(IPAddress address)
    {
        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            // RFC 1918 private space, and the 0.0.0.0/8 "this network" block.
            0 or 10 => false,

            // Carrier-grade NAT, RFC 6598.
            100 when octets[1] is >= 64 and <= 127 => false,

            // Link-local, RFC 3927. This is the range cloud metadata services live in and the single
            // most valuable target an SSRF redirect has.
            169 when octets[1] == 254 => false,
            172 when octets[1] is >= 16 and <= 31 => false,
            192 when octets[1] == 168 => false,

            // Benchmarking, RFC 2544.
            198 when octets[1] is 18 or 19 => false,

            // Multicast, and the reserved 240.0.0.0/4 block above it.
            >= 224 => false,
            _ => true,
        };
    }

    private static bool IsAllowedV6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        // Unique local addresses, fc00::/7. There is no framework predicate for these.
        var bytes = address.GetAddressBytes();
        return (bytes[0] & 0xFE) != 0xFC;
    }
}
