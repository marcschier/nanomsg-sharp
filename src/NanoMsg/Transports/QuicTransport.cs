// Copyright (c) marcschier. Licensed under the MIT License.

#if NET8_0_OR_GREATER

// System.Net.Quic is shipped as an [Experimental] API (SYSLIB5001). The quic:// transport opts in
// deliberately; the diagnostic is suppressed for this file only so the rest of the build keeps it on.
// CA1416 (platform compatibility) is likewise suppressed here: QUIC is supported on Windows/Linux/macOS
// only, and every entry point guards on QuicListener/QuicConnection.IsSupported and throws
// PlatformNotSupportedException otherwise — a runtime guard the analyzer cannot see across the
// connection-options callback and the listener/connection helper types.
#pragma warning disable SYSLIB5001
#pragma warning disable CA1416
#pragma warning disable CA2252

using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// The QUIC transport (<c>quic://</c>, with optional <c>quic4</c>/<c>quic6</c> suffixes), built on the
/// in-box <see cref="System.Net.Quic"/> stack. Each connection carries the Scalability Protocols over a
/// single bidirectional QUIC stream, reusing the same 8-byte SP handshake and length-prefix framing as
/// the TCP and TLS transports. QUIC is always encrypted, so binding requires
/// <see cref="NanoSocketOptions.TlsServerCertificate"/> and dialing validates the server certificate via
/// <see cref="NanoSocketOptions.TlsRemoteValidationCallback"/>.
/// <para>
/// Requires .NET 8+ and a working MsQuic provider (bundled with Windows 11 / Windows Server 2022, or the
/// <c>libmsquic</c> package on Linux). When MsQuic is unavailable, <c>bind</c>/<c>connect</c> throw
/// <see cref="PlatformNotSupportedException"/>; on <c>netstandard</c> targets the transport is unavailable.
/// </para>
/// </summary>
internal sealed class QuicTransport : INanoTransport
{
    /// <summary>The ALPN protocol identifier negotiated for SP-over-QUIC connections.</summary>
    internal static readonly SslApplicationProtocol Alpn = new("nmsg-sp");

    private const long StreamAbortErrorCode = 0x1;
    private const long ConnectionCloseErrorCode = 0x0;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
#if NET9_0_OR_GREATER
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
#endif

    private const string UnsupportedMessage =
        "The 'quic' transport requires MsQuic. Install it (bundled with Windows 11 / Windows Server 2022, " +
        "or the 'libmsquic' package on Linux) or use another transport.";

    /// <inheritdoc/>
    public async ValueTask<INanoListener> BindAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        if (!QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException(UnsupportedMessage);
        }

        if (options.TlsServerCertificate is null)
        {
            throw new NanoMsgException("Binding a 'quic' endpoint requires NanoSocketOptions.TlsServerCertificate.");
        }

        IPAddress ip = TcpEndpoints.BindAddress(address);
        QuicServerConnectionOptions connectionOptions = new()
        {
            DefaultStreamErrorCode = StreamAbortErrorCode,
            DefaultCloseErrorCode = ConnectionCloseErrorCode,
            IdleTimeout = IdleTimeout,
            MaxInboundBidirectionalStreams = 1,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [Alpn],
                ServerCertificate = options.TlsServerCertificate,
                ClientCertificateRequired = false,
            },
        };

        QuicListenerOptions listenerOptions = new()
        {
            ListenEndPoint = new IPEndPoint(ip, address.Port),
            ApplicationProtocols = [Alpn],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(connectionOptions),
        };

        try
        {
            QuicListener listener = await QuicListener.ListenAsync(listenerOptions, cancellationToken)
                .ConfigureAwait(false);
            return new QuicNanoListener(listener);
        }
        catch (Exception ex) when (ex is QuicException or SocketException or IOException)
        {
            throw new NanoMsgException($"Failed to bind '{address.Original}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException(UnsupportedMessage);
        }

        IPEndPoint remote = await TcpEndpoints.ResolveRemoteAsync(address, cancellationToken).ConfigureAwait(false);
        QuicClientConnectionOptions clientOptions = new()
        {
            RemoteEndPoint = remote,
            DefaultStreamErrorCode = StreamAbortErrorCode,
            DefaultCloseErrorCode = ConnectionCloseErrorCode,
            IdleTimeout = IdleTimeout,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [Alpn],
                TargetHost = options.TlsTargetHost ?? address.Host,
                ClientCertificates = options.TlsClientCertificates,
                RemoteCertificateValidationCallback = options.TlsRemoteValidationCallback,
            },
        };
#if NET9_0_OR_GREATER
        clientOptions.KeepAliveInterval = KeepAliveInterval;
#endif

        QuicConnection connection;
        try
        {
            connection = await QuicConnection.ConnectAsync(clientOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is QuicException or AuthenticationException or SocketException or IOException)
        {
            throw new NanoMsgException($"QUIC handshake failed for '{address.Original}': {ex.Message}", ex);
        }

        try
        {
            QuicStream stream = await connection
                .OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);
            return new QuicNanoConnection(connection, stream);
        }
        catch (Exception ex) when (ex is QuicException or IOException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new NanoMsgException($"Failed to open a QUIC stream for '{address.Original}': {ex.Message}", ex);
        }
    }
}

/// <summary>An <see cref="INanoListener"/> that accepts one bidirectional QUIC stream per connection.</summary>
internal sealed class QuicNanoListener : INanoListener
{
    private readonly QuicListener _listener;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="QuicNanoListener"/> class.</summary>
    /// <param name="listener">The bound QUIC listener.</param>
    public QuicNanoListener(QuicListener listener) => _listener = listener;

    /// <inheritdoc/>
    public int Port => _listener.LocalEndPoint.Port;

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        QuicConnection connection = await _listener.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            QuicStream stream = await connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
            return new QuicNanoConnection(connection, stream);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Adapts a bidirectional <see cref="QuicStream"/> into an <see cref="INanoConnection"/> by layering
/// <see cref="PipeReader"/>/<see cref="PipeWriter"/> over it. Owns and disposes both the stream and its
/// parent <see cref="QuicConnection"/>.
/// </summary>
internal sealed class QuicNanoConnection : INanoConnection
{
    private readonly QuicConnection _connection;
    private readonly QuicStream _stream;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="QuicNanoConnection"/> class.</summary>
    /// <param name="connection">The owned QUIC connection.</param>
    /// <param name="stream">The owned bidirectional QUIC stream.</param>
    public QuicNanoConnection(QuicConnection connection, QuicStream stream)
    {
        _connection = connection;
        _stream = stream;
        Input = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <inheritdoc/>
    public PipeReader Input { get; }

    /// <inheritdoc/>
    public PipeWriter Output { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Input.CompleteAsync().ConfigureAwait(false);
        await Output.CompleteAsync().ConfigureAwait(false);

        try
        {
            _stream.CompleteWrites();
        }
        catch (Exception ex) when (ex is QuicException or ObjectDisposedException or InvalidOperationException)
        {
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

#else

using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// Placeholder for the QUIC transport on target frameworks without <c>System.Net.Quic</c> (every
/// <c>netstandard</c> target). Binding or dialing a <c>quic://</c> endpoint throws
/// <see cref="PlatformNotSupportedException"/>; QUIC requires .NET 8 or later.
/// </summary>
internal sealed class QuicTransport : INanoTransport
{
    private const string UnsupportedMessage =
        "The 'quic' transport requires .NET 8 or later (System.Net.Quic); it is not available on this " +
        "target framework.";

    /// <inheritdoc/>
    public ValueTask<INanoListener> BindAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException(UnsupportedMessage);

    /// <inheritdoc/>
    public ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException(UnsupportedMessage);
}

#endif
