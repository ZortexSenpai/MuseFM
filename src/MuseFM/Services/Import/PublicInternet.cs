using System.Net;
using System.Net.Sockets;

namespace MuseFM.Services.Import;

/// <summary>A request was about to connect to an address that is not on the public internet.</summary>
public sealed class PrivateAddressException(string host) : Exception($"{host} is not a public internet address.");

/// <summary>
/// Keeps requests to addresses that users type in away from the server's own network: loopback, private ranges,
/// link-local (cloud metadata) and the like. Without it, anyone with an account could use MuseFM to reach services
/// that only the server can see.
/// </summary>
public static class PublicInternet
{
    /// <summary>
    /// A handler that only connects to public addresses. The check runs for every connection, on the address that is
    /// actually connected to, so redirects and names that resolve to a private address are covered as well.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = ConnectAsync,
        // Through a proxy only the proxy's address would be checked.
        UseProxy = false,
        MaxAutomaticRedirections = 5,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0) throw new PrivateAddressException(host);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>True when a failed request was stopped by <see cref="CreateHandler"/>.</summary>
    public static bool Blocked(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is PrivateAddressException) return true;
        return false;
    }

    /// <summary>Whether an address is an ordinary host on the internet, as opposed to a local, private or reserved one.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] switch
            {
                0 or 10 or 127 => false, // "this" network, private, loopback
                100 => b[1] is < 64 or > 127, // carrier-grade NAT
                169 => b[1] != 254, // link-local, where cloud providers serve instance metadata
                172 => b[1] is < 16 or > 31, // private
                192 => !(b[1] == 168 || (b[1] == 0 && b[2] == 0)), // private, protocol assignments
                198 => b[1] is not (18 or 19), // benchmarking
                >= 224 => false, // multicast, reserved, broadcast
                _ => true,
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast) return false;
        // "::" and "::1", and the retired "::a.b.c.d" form of IPv4 addresses.
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0) return false;
        // NAT64 (64:ff9b::/96) leads to the IPv4 address in the last four bytes.
        if (b is [0x00, 0x64, 0xff, 0x9b, ..] && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0) return IsPublic(new IPAddress(b.AsSpan(12)));
        return true;
    }
}
