// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// The TLS-over-TCP transport (<c>tls+tcp://</c>). It dials/accepts a plain TCP connection (via
/// <see cref="TcpEndpoints"/>), performs the TLS handshake with an <see cref="SslStream"/>, and then
/// reuses the same SP handshake and length-prefix framing as the plaintext TCP transport. Binding
/// requires <see cref="NanoSocketOptions.TlsServerCertificate"/>.
/// </summary>
internal sealed class TlsTransport : INanoTransport
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
                "Binding a 'tls+tcp' endpoint requires NanoSocketOptions.TlsServerCertificate.");
        }

        Socket socket = TcpEndpoints.Listen(address);
        return new ValueTask<INanoListener>(new TlsSocketListener(socket, options));
    }

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        Socket socket = await TcpEndpoints.ConnectAsync(address, options.TcpNoDelay, cancellationToken)
            .ConfigureAwait(false);
        SslStream ssl = new(
            new NetworkStream(socket, ownsSocket: true),
            leaveInnerStreamOpen: false,
            options.TlsRemoteValidationCallback);
        try
        {
#if NETSTANDARD2_0
            await ssl.AuthenticateAsClientAsync(
                options.TlsTargetHost ?? address.Host,
                options.TlsClientCertificates,
                SslProtocols.None,
                checkCertificateRevocation: false).ConfigureAwait(false);
#else
            SslClientAuthenticationOptions auth = new()
            {
                TargetHost = options.TlsTargetHost ?? address.Host,
                ClientCertificates = options.TlsClientCertificates,
            };
            await ssl.AuthenticateAsClientAsync(auth, cancellationToken).ConfigureAwait(false);
#endif
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new NanoMsgException($"TLS handshake failed for '{address.Original}': {ex.Message}", ex);
        }

        return new StreamConnection(ssl);
    }
}

/// <summary>An <see cref="INanoListener"/> that completes a TLS server handshake on each accepted socket.</summary>
internal sealed class TlsSocketListener : INanoListener
{
    private readonly Socket _socket;
    private readonly NanoSocketOptions _options;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="TlsSocketListener"/> class.</summary>
    /// <param name="socket">A bound, listening socket.</param>
    /// <param name="options">Socket options carrying the TLS server certificate.</param>
    public TlsSocketListener(Socket socket, NanoSocketOptions options)
    {
        _socket = socket;
        _options = options;
    }

    /// <inheritdoc/>
    public int Port => (_socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        Socket accepted = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        if (_options.TcpNoDelay)
        {
            accepted.NoDelay = true;
        }

        SslStream ssl = new(
            new NetworkStream(accepted, ownsSocket: true),
            leaveInnerStreamOpen: false,
            _options.TlsRemoteValidationCallback);
        try
        {
#if NETSTANDARD2_0
            await ssl.AuthenticateAsServerAsync(
                _options.TlsServerCertificate,
                clientCertificateRequired: false,
                SslProtocols.None,
                checkCertificateRevocation: false).ConfigureAwait(false);
#else
            SslServerAuthenticationOptions auth = new()
            {
                ServerCertificate = _options.TlsServerCertificate,
                ClientCertificateRequired = false,
            };
            await ssl.AuthenticateAsServerAsync(auth, cancellationToken).ConfigureAwait(false);
#endif
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new NanoMsgException($"TLS server handshake failed: {ex.Message}", ex);
        }

        return new StreamConnection(ssl);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _socket.Dispose();
        }

        return default;
    }
}
