// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using Dtls;
using Dtls.Transport;
using NanoMsg.Transports;
using NanoMsg.Wire;

namespace NanoMsg.Dtls;

/// <summary>
/// The DTLS-over-UDP datagram transport (<c>dtls+udp://</c>, with optional <c>dtls+udp4</c>/<c>dtls+udp6</c>
/// suffixes). It carries each SP message as one confidential, authenticated DTLS application datagram,
/// reusing the core <see cref="DatagramConnection"/> re-framer. Binding requires a server certificate
/// (<see cref="NanoSocketOptions.TlsServerCertificate"/>); dialing validates the server certificate via
/// <see cref="NanoSocketOptions.TlsRemoteValidationCallback"/>. Registered for <see cref="AddressScheme.DtlsUdp"/>
/// by <see cref="NanoMsgDtls"/>.
/// </summary>
internal sealed class DtlsUdpTransport : INanoTransport
{
    /// <inheritdoc/>
    public ValueTask<INanoListener> BindAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        if (options.TlsServerCertificate is null)
        {
            throw new NanoMsgException(
                "Binding a 'dtls+udp' endpoint requires NanoSocketOptions.TlsServerCertificate.");
        }

        IPAddress ip = TcpEndpoints.BindAddress(address);
        Socket socket = new(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(ip, address.Port));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new NanoMsgException($"Failed to bind '{address.Original}': {ex.Message}", ex);
        }

        return new ValueTask<INanoListener>(new DtlsUdpListener(socket, options, localProtocol));
    }

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        IPEndPoint remote = await TcpEndpoints.ResolveRemoteAsync(address, cancellationToken).ConfigureAwait(false);
        Socket socket = new(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        UdpDatagramTransport transport;
        try
        {
            await socket.ConnectAsync(remote, cancellationToken).ConfigureAwait(false);
            transport = new UdpDatagramTransport(socket, ownsSocket: true);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new NanoMsgException($"Failed to connect '{address.Original}': {ex.Message}", ex);
        }

        DtlsConnection connection;
        try
        {
            connection = await DtlsClient
                .ConnectAsync(transport, DtlsSpExchange.BuildClientOptions(options, address.Host), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DtlsException or IOException or SocketException or OperationCanceledException)
        {
            transport.Dispose();
            throw new NanoMsgException($"DTLS handshake failed for '{address.Original}': {ex.Message}", ex);
        }

        try
        {
            SpProtocol peerProtocol = await DtlsSpExchange
                .PerformAsync(connection, localProtocol, cancellationToken).ConfigureAwait(false);
            return new DatagramConnection(new DtlsDatagramChannel(connection, transport), peerProtocol);
        }
        catch
        {
            connection.Dispose();
            transport.Dispose();
            throw;
        }
    }
}
