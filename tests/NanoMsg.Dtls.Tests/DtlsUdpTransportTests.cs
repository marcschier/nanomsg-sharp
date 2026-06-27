// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NanoMsg.Dtls.Tests;

public sealed class DtlsUdpTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Before(Class)]
    public static void EnsureRegistered() => NanoMsgDtls.Register();

    [Test]
    public async Task DtlsUdp_push_pull_roundtrips()
    {
        using X509Certificate2 cert = CreateSelfSignedCertificate();
        NanoSocketOptions serverOptions = new() { TlsServerCertificate = cert };
        NanoSocketOptions clientOptions = new() { TlsRemoteValidationCallback = AcceptAnyCertificate() };

        await using PullSocket pull = new(serverOptions);
        await using PushSocket push = new(clientOptions);
        int port = await pull.BindAsync("dtls+udp://127.0.0.1:0");
        push.Connect($"dtls+udp://127.0.0.1:{port}");
        await push.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await push.SendAsync("over-dtls"u8.ToArray(), cts.Token);
        using NanoMessage message = await pull.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("over-dtls");
    }

    [Test]
    public async Task DtlsUdp_pair_exchanges_both_ways()
    {
        using X509Certificate2 cert = CreateSelfSignedCertificate();
        NanoSocketOptions serverOptions = new() { TlsServerCertificate = cert };
        NanoSocketOptions clientOptions = new() { TlsRemoteValidationCallback = AcceptAnyCertificate() };

        await using PairSocket server = new(serverOptions);
        await using PairSocket client = new(clientOptions);
        int port = await server.BindAsync("dtls+udp://127.0.0.1:0");
        client.Connect($"dtls+udp://127.0.0.1:{port}");
        await server.WaitForConnectionsAsync(1, Timeout);
        await client.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await client.SendAsync("ping"u8.ToArray(), cts.Token);
        using (NanoMessage forward = await server.ReceiveAsync(cts.Token))
        {
            await Assert.That(Encoding.ASCII.GetString(forward.Span)).IsEqualTo("ping");
        }

        await server.SendAsync("pong"u8.ToArray(), cts.Token);
        using NanoMessage backward = await client.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(backward.Span)).IsEqualTo("pong");
    }

    [Test]
    public async Task Binding_dtls_without_certificate_throws()
    {
        await using PullSocket pull = new();
        bool threw = false;
        try
        {
            await pull.BindAsync("dtls+udp://127.0.0.1:0");
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5359:Do not disable certificate validation",
        Justification = "Tests use an in-memory self-signed certificate over loopback.")]
    private static RemoteCertificateValidationCallback AcceptAnyCertificate() => (_, _, _, _) => true;

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=nanomsg-sharp-dtls-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        SubjectAlternativeNameBuilder san = new();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
#endif
    }
}
