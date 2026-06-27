// Copyright (c) marcschier. Licensed under the MIT License.

using System.Collections.Concurrent;

namespace NanoMsg.Transports;

/// <summary>
/// Resolves an <see cref="AddressScheme"/> to its <see cref="INanoTransport"/> implementation. Built-in
/// transports are resolved directly; additional transports (for example the UDP and DTLS datagram
/// transports in the <c>NanoMsgSharp.Extensions</c> package) register themselves via
/// <see cref="Register"/>.
/// </summary>
internal static class TransportFactory
{
    private static readonly ConcurrentDictionary<AddressScheme, Func<INanoTransport>> Registered = new();

    /// <summary>Registers a transport factory for an extension <paramref name="scheme"/>.</summary>
    /// <param name="scheme">The scheme served by <paramref name="factory"/>.</param>
    /// <param name="factory">Creates a fresh transport instance per endpoint.</param>
    public static void Register(AddressScheme scheme, Func<INanoTransport> factory) =>
        Registered[scheme] = factory;

    /// <summary>Gets the transport for <paramref name="scheme"/>.</summary>
    /// <param name="scheme">The endpoint scheme.</param>
    /// <returns>A transport implementation.</returns>
    /// <exception cref="NanoMsgException">The scheme has no available transport.</exception>
    public static INanoTransport For(AddressScheme scheme) => scheme switch
    {
        AddressScheme.InProc => new InProcTransport(),
        AddressScheme.Tcp => new TcpTransport(),
        AddressScheme.TlsTcp => new TlsTransport(),
        AddressScheme.Ipc => new IpcTransport(),
        AddressScheme.Ws => new WsTransport(),
        AddressScheme.Wss => new WsTransport(),
        AddressScheme.Udp => new UdpTransport(),
        _ => ForRegistered(scheme),
    };

    private static INanoTransport ForRegistered(AddressScheme scheme)
    {
        if (Registered.TryGetValue(scheme, out Func<INanoTransport>? factory))
        {
            return factory();
        }

        if (scheme is AddressScheme.DtlsUdp)
        {
            throw new NanoMsgException(
                "The 'dtls+udp' transport requires the NanoMsgSharp.Dtls package. Reference it and call " +
                "NanoMsgDtls.Register() (or ensure its module initializer runs) before use.");
        }

        throw new NanoMsgException($"Unsupported transport scheme '{scheme}'.");
    }
}
