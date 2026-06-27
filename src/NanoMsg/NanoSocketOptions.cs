// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace NanoMsg;

/// <summary>
/// Tunable per-socket options shared by every scalability protocol and transport. Defaults mirror
/// the reference nanomsg values where one exists.
/// </summary>
public sealed class NanoSocketOptions
{
    /// <summary>Gets or sets the send-operation timeout; <see langword="null"/> waits indefinitely.</summary>
    public TimeSpan? SendTimeout { get; set; }

    /// <summary>Gets or sets the receive-operation timeout; <see langword="null"/> waits indefinitely.</summary>
    public TimeSpan? ReceiveTimeout { get; set; }

    /// <summary>
    /// Gets or sets the initial reconnect interval after a connection drops (nanomsg default: 100 ms).
    /// </summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the maximum reconnect interval (exponential backoff cap); <see cref="TimeSpan.Zero"/>
    /// disables growth.
    /// </summary>
    public TimeSpan ReconnectIntervalMax { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets the linger period applied on close to flush pending sends (nanomsg default: 1 s).
    /// </summary>
    public TimeSpan LingerTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the outbound queue depth in messages; 0 means unbounded.</summary>
    public int SendHighWatermark { get; set; }

    /// <summary>Gets or sets the inbound queue depth in messages; 0 means unbounded.</summary>
    public int ReceiveHighWatermark { get; set; }

    /// <summary>
    /// Gets or sets the maximum accepted inbound message size in bytes (nanomsg default: 1 MiB); negative
    /// means unlimited.
    /// </summary>
    public long ReceiveMaxMessageSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the interval after which an unanswered REQ request is resent (nanomsg default: 60 s).
    /// </summary>
    public TimeSpan RequestResendInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets how long a surveyor collects responses before the survey ends (nanomsg default: 1 s).
    /// </summary>
    public TimeSpan SurveyDeadline { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets a value indicating whether to disable Nagle's algorithm on TCP connections.</summary>
    public bool TcpNoDelay { get; set; }

    /// <summary>
    /// Gets or sets the certificate (with private key) presented when accepting <c>tls+tcp</c>/<c>wss</c>
    /// connections. Required to bind a TLS endpoint.
    /// </summary>
    public X509Certificate2? TlsServerCertificate { get; set; }

    /// <summary>
    /// Gets or sets the client certificates offered when dialing a <c>tls+tcp</c>/<c>wss</c> endpoint.
    /// </summary>
    public X509CertificateCollection? TlsClientCertificates { get; set; }

    /// <summary>
    /// Gets or sets the callback that validates the remote certificate during a TLS handshake;
    /// <see langword="null"/> uses the platform default (chain + host-name validation).
    /// </summary>
    public RemoteCertificateValidationCallback? TlsRemoteValidationCallback { get; set; }

    /// <summary>
    /// Gets or sets the host name used for TLS server-name indication and certificate validation when
    /// dialing; defaults to the address host when <see langword="null"/>.
    /// </summary>
    public string? TlsTargetHost { get; set; }
}
