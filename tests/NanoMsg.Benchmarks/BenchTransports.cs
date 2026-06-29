// Copyright (c) marcschier. Licensed under the MIT License.

// QuicConnection.IsSupported is part of the [Experimental]/preview System.Net.Quic surface; only
// probed here to skip the quic rows where MsQuic is unavailable.
#pragma warning disable SYSLIB5001
#pragma warning disable CA2252
#pragma warning disable CA1416

using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NanoMsg.Dtls;

namespace NanoMsg.Benchmarks;

/// <summary>The scalability protocol exercised by a matrix benchmark.</summary>
public enum BenchProtocol
{
    /// <summary>One-way load-balanced pipeline (PUSH → PULL).</summary>
    PushPull,

    /// <summary>Request/reply round trip.</summary>
    ReqRep,

    /// <summary>One-way publish/subscribe (subscriber accepts all topics).</summary>
    PubSub,

    /// <summary>One-way over a bidirectional PAIR.</summary>
    Pair,

    /// <summary>One-way over an NNG PAIR1.</summary>
    Pair1,

    /// <summary>Surveyor → respondent round trip.</summary>
    Survey,

    /// <summary>One-way over a BUS.</summary>
    Bus,
}

/// <summary>The transport exercised by a matrix benchmark.</summary>
public enum BenchTransport
{
    /// <summary>In-process.</summary>
    Inproc,

    /// <summary>TCP.</summary>
    Tcp,

    /// <summary>Unix-domain socket / named pipe.</summary>
    Ipc,

    /// <summary>WebSocket.</summary>
    Ws,

    /// <summary>TLS over TCP.</summary>
    TlsTcp,

    /// <summary>Secure WebSocket.</summary>
    Wss,

    /// <summary>UDP datagram.</summary>
    Udp,

    /// <summary>QUIC.</summary>
    Quic,

    /// <summary>DTLS over UDP.</summary>
    DtlsUdp,
}

/// <summary>Shared transport/cert/capability helpers for the matrix benchmarks.</summary>
internal static class BenchTransports
{
    /// <summary>Datagram transports cap a single SP message near 65000 bytes.</summary>
    public const int DatagramMaxSize = 65000;

    /// <summary>Ensures the DTLS transport (dtls+udp) is registered.</summary>
    public static void EnsureDtls() => NanoMsgDtls.Register();

    /// <summary>Whether a transport carries arbitrarily large messages (vs. one datagram per message).</summary>
    public static bool IsStream(BenchTransport t) => t is not (BenchTransport.Udp or BenchTransport.DtlsUdp);

    /// <summary>Whether a transport can run on this machine (MsQuic for quic; always for the rest).</summary>
    public static bool IsSupported(BenchTransport t) =>
        t is not BenchTransport.Quic || QuicConnection.IsSupported;

    /// <summary>Whether the transport is TLS-secured and so needs a server certificate.</summary>
    public static bool NeedsCertificate(BenchTransport t) =>
        t is BenchTransport.TlsTcp or BenchTransport.Wss or BenchTransport.Quic or BenchTransport.DtlsUdp;

    /// <summary>Builds a unique bind/connect address for the transport.</summary>
    public static string Address(BenchTransport t)
    {
        string id = Guid.NewGuid().ToString("N");
        return t switch
        {
            BenchTransport.Inproc => $"inproc://bench-{id}",
            BenchTransport.Ipc => OperatingSystem.IsWindows()
                ? $"ipc://bench-{id}"
                : $"ipc://{Path.Combine(Path.GetTempPath(), $"bench-{id}.sock")}",
            BenchTransport.Tcp => "tcp://127.0.0.1:0",
            BenchTransport.TlsTcp => "tls+tcp://127.0.0.1:0",
            BenchTransport.Ws => "ws://127.0.0.1:0",
            BenchTransport.Wss => "wss://127.0.0.1:0",
            BenchTransport.Udp => "udp://127.0.0.1:0",
            BenchTransport.Quic => "quic://127.0.0.1:0",
            BenchTransport.DtlsUdp => "dtls+udp://127.0.0.1:0",
            _ => throw new ArgumentOutOfRangeException(nameof(t)),
        };
    }

    /// <summary>The connect address for a peer of a server bound at <paramref name="boundAddress"/>.</summary>
    public static string Connect(BenchTransport t, string boundAddress, int port) => t switch
    {
        BenchTransport.Inproc or BenchTransport.Ipc => boundAddress,
        _ => $"{Scheme(t)}://127.0.0.1:{port}",
    };

    /// <summary>Server-side options (carries the server certificate for TLS transports).</summary>
    public static NanoSocketOptions ServerOptions(BenchTransport t, X509Certificate2 cert) =>
        NeedsCertificate(t) ? new NanoSocketOptions { TlsServerCertificate = cert } : new NanoSocketOptions();

    /// <summary>Client-side options (accepts the self-signed cert for TLS transports).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5359:Do not disable certificate validation",
        Justification = "Benchmarks use an in-memory self-signed certificate over loopback.")]
    public static NanoSocketOptions ClientOptions(BenchTransport t) => NeedsCertificate(t)
        ? new NanoSocketOptions { TlsRemoteValidationCallback = (_, _, _, _) => true }
        : new NanoSocketOptions();

    private static string Scheme(BenchTransport t) => t switch
    {
        BenchTransport.Tcp => "tcp",
        BenchTransport.TlsTcp => "tls+tcp",
        BenchTransport.Ws => "ws",
        BenchTransport.Wss => "wss",
        BenchTransport.Udp => "udp",
        BenchTransport.Quic => "quic",
        BenchTransport.DtlsUdp => "dtls+udp",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    /// <summary>Creates a loopback self-signed certificate persisted so server TLS accepts it.</summary>
    public static X509Certificate2 CreateCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=nanomsg-sharp-bench", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        SubjectAlternativeNameBuilder san = new();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }
}
