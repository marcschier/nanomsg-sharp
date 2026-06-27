// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;

namespace NanoMsg.Transports;

/// <summary>Shared TCP bind/connect helpers used by the plaintext and TLS TCP transports.</summary>
internal static class TcpEndpoints
{
    /// <summary>The listen backlog.</summary>
    public const int Backlog = 128;

    /// <summary>Creates a bound, listening TCP socket for <paramref name="address"/>.</summary>
    /// <param name="address">The bind address (host <c>*</c> binds all interfaces; port 0 = ephemeral).</param>
    /// <returns>The listening socket.</returns>
    public static Socket Listen(NanoAddress address)
    {
        IPAddress ip = ResolveBindAddress(address);
        Socket socket = new(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(ip, address.Port));
            socket.Listen(Backlog);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new NanoMsgException($"Failed to bind '{address.Original}': {ex.Message}", ex);
        }

        return socket;
    }

    /// <summary>Resolves and dials <paramref name="address"/>, returning the connected socket.</summary>
    /// <param name="address">The remote address.</param>
    /// <param name="noDelay">Whether to disable Nagle's algorithm.</param>
    /// <param name="cancellationToken">A token used to cancel the connect.</param>
    /// <returns>The connected socket.</returns>
    public static async ValueTask<Socket> ConnectAsync(
        NanoAddress address,
        bool noDelay,
        CancellationToken cancellationToken)
    {
        IPEndPoint endpoint = await ResolveConnectEndpointAsync(address, cancellationToken).ConfigureAwait(false);
        Socket socket = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = noDelay,
        };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new NanoMsgException($"Failed to connect '{address.Original}': {ex.Message}", ex);
        }

        return socket;
    }

    private static IPAddress ResolveBindAddress(NanoAddress address)
    {
        if (address.IsWildcardHost || address.Host.Length == 0)
        {
            return WildcardBindAddress(address.FamilyPreference);
        }

        if (IPAddress.TryParse(address.Host, out IPAddress? ip))
        {
            return ip;
        }

        throw new NanoMsgException($"Bind host '{address.Host}' must be an IP address or '*'.");
    }

    /// <summary>Resolves a bind <see cref="IPAddress"/> for <paramref name="address"/> (wildcard or literal).</summary>
    /// <param name="address">The bind address.</param>
    /// <returns>The local <see cref="IPAddress"/> to bind.</returns>
    public static IPAddress BindAddress(NanoAddress address) => ResolveBindAddress(address);

    /// <summary>Resolves the remote <see cref="IPEndPoint"/> for <paramref name="address"/> (literal or DNS).</summary>
    /// <param name="address">The remote address.</param>
    /// <param name="cancellationToken">A token used to cancel resolution.</param>
    /// <returns>The resolved remote endpoint, honouring any IPv4/IPv6 preference.</returns>
    public static ValueTask<IPEndPoint> ResolveRemoteAsync(
        NanoAddress address,
        CancellationToken cancellationToken) => ResolveConnectEndpointAsync(address, cancellationToken);

    /// <summary>Gets the wildcard bind address for a family preference (IPv6-any unless IPv4 is forced).</summary>
    /// <param name="family">The parsed IPv4/IPv6 preference.</param>
    /// <returns>The wildcard <see cref="IPAddress"/>.</returns>
    public static IPAddress WildcardBindAddress(AddressFamilyPreference family) =>
        family == AddressFamilyPreference.IPv4 ? IPAddress.Any : IPAddress.IPv6Any;

    private static async ValueTask<IPEndPoint> ResolveConnectEndpointAsync(
        NanoAddress address,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(address.Host, out IPAddress? literal))
        {
            return new IPEndPoint(literal, address.Port);
        }

#if NETSTANDARD2_0 || NETSTANDARD2_1
        IPAddress[] addresses = await PlatformPolyfills
            .GetHostAddressesAsync(address.Host, cancellationToken).ConfigureAwait(false);
#else
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(address.Host, cancellationToken).ConfigureAwait(false);
#endif
        IPAddress? selected = SelectByFamily(addresses, address.FamilyPreference);
        if (selected is null)
        {
            throw new NanoMsgException($"Host '{address.Host}' did not resolve to a usable address.");
        }

        return new IPEndPoint(selected, address.Port);
    }

    private static IPAddress? SelectByFamily(IPAddress[] addresses, AddressFamilyPreference family)
    {
        AddressFamily? required = family switch
        {
            AddressFamilyPreference.IPv4 => AddressFamily.InterNetwork,
            AddressFamilyPreference.IPv6 => AddressFamily.InterNetworkV6,
            _ => null,
        };

        foreach (IPAddress candidate in addresses)
        {
            if (required is null || candidate.AddressFamily == required)
            {
                return candidate;
            }
        }

        return null;
    }
}
