// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// The WebSocket transport (<c>ws://</c> and, over TLS, <c>wss://</c>). It implements the
/// SP-over-WebSocket mapping used by nanomsg and NNG: the SP protocol is negotiated through the
/// <c>Sec-WebSocket-Protocol</c> sub-protocol (<c>&lt;name&gt;.sp.nanomsg.org</c>) and each SP message
/// is carried as one binary WebSocket message (see <see cref="WebSocketConnection"/>). <c>connect</c>
/// dials with a <see cref="ClientWebSocket"/>; <c>bind</c> runs a manual <see cref="TcpListener"/>
/// (optionally wrapped in <see cref="SslStream"/>) that performs the HTTP upgrade by hand, so the same
/// code serves both <c>ws</c> and <c>wss</c>.
/// </summary>
internal sealed class WsTransport : INanoTransport
{
    /// <inheritdoc/>
    public ValueTask<INanoListener> BindAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        bool secure = address.Scheme == AddressScheme.Wss;
        if (secure && options.TlsServerCertificate is null)
        {
            throw new NanoMsgException(
                "Binding a 'wss' endpoint requires NanoSocketOptions.TlsServerCertificate.");
        }

        IPAddress ip = address.IsWildcardHost || address.Host.Length == 0
            ? TcpEndpoints.WildcardBindAddress(address.FamilyPreference)
            : ParseHost(address.Host);
        TcpListener listener = new(ip, address.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new NanoMsgException($"Failed to bind '{address.Original}': {ex.Message}", ex);
        }

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new ValueTask<INanoListener>(new WsListener(listener, port, options, localProtocol, secure));
    }

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        bool secure = address.Scheme == AddressScheme.Wss;
        SpProtocol peerProtocol = localProtocol.Counterpart();
        ClientWebSocket client = new();
        client.Options.AddSubProtocol($"{peerProtocol.WireName()}.sp.nanomsg.org");
        if (secure)
        {
            if (options.TlsRemoteValidationCallback is not null)
            {
                client.Options.RemoteCertificateValidationCallback = options.TlsRemoteValidationCallback;
            }

            if (options.TlsClientCertificates is not null)
            {
                client.Options.ClientCertificates = options.TlsClientCertificates;
            }
        }

        string scheme = secure ? "wss" : "ws";
        Uri uri = new($"{scheme}://{address.Host}:{address.Port}{address.Path}");
        try
        {
            await client.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or SocketException
            or AuthenticationException)
        {
            client.Dispose();
            throw new NanoMsgException($"Failed to connect '{address.Original}': {ex.Message}", ex);
        }

        return new WebSocketConnection(client, peerProtocol);
    }

    private static IPAddress ParseHost(string host) =>
        IPAddress.TryParse(host, out IPAddress? ip)
            ? ip
            : throw new NanoMsgException($"WebSocket bind host '{host}' must be an IP address or '*'.");
}

/// <summary>
/// An <see cref="INanoListener"/> that accepts raw TCP sockets, optionally negotiates TLS, performs
/// the WebSocket HTTP upgrade by hand, and surfaces each upgraded peer as a <see cref="WebSocketConnection"/>.
/// Upgrades run concurrently in a background loop so a slow client cannot stall <see cref="AcceptAsync"/>.
/// </summary>
internal sealed class WsListener : INanoListener
{
    private readonly TcpListener _listener;
    private readonly NanoSocketOptions _options;
    private readonly bool _secure;
    private readonly string _subProtocol;
    private readonly SpProtocol _peerProtocol;
    private readonly System.Threading.Channels.Channel<INanoConnection> _accepted =
        System.Threading.Channels.Channel.CreateUnbounded<INanoConnection>();

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="WsListener"/> class.</summary>
    /// <param name="listener">A started TCP listener.</param>
    /// <param name="port">The bound port.</param>
    /// <param name="options">Socket options (TLS certificate for <c>wss</c>, no-delay).</param>
    /// <param name="localProtocol">The local SP protocol, used to negotiate the sub-protocol.</param>
    /// <param name="secure">Whether this endpoint negotiates TLS (<c>wss</c>).</param>
    public WsListener(
        TcpListener listener,
        int port,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        bool secure)
    {
        _listener = listener;
        Port = port;
        _options = options;
        _secure = secure;
        _subProtocol = $"{localProtocol.WireName()}.sp.nanomsg.org";
        _peerProtocol = localProtocol.Counterpart();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <inheritdoc/>
    public int Port { get; }

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> AcceptAsync(CancellationToken cancellationToken) =>
        await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _accepted.Writer.TryComplete();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
                _ = UpgradeAsync(socket, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task UpgradeAsync(Socket socket, CancellationToken cancellationToken)
    {
        if (_options.TcpNoDelay)
        {
            socket.NoDelay = true;
        }

        Stream stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            if (_secure)
            {
                SslStream ssl = new(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _options.TlsServerCertificate,
                        ClientCertificateRequired = false,
                    },
                    cancellationToken).ConfigureAwait(false);
                stream = ssl;
            }

            WebSocket webSocket = await WsHandshake
                .AcceptAsync(stream, _subProtocol, cancellationToken).ConfigureAwait(false);
            await _accepted.Writer
                .WriteAsync(new WebSocketConnection(webSocket, _peerProtocol), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NanoMsgException or IOException or SocketException
            or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
