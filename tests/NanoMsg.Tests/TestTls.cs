// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NanoMsg.Tests;

/// <summary>Shared TLS helpers for the transport tests (in-memory self-signed certificate over loopback).</summary>
internal static class TestTls
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5359:Do not disable certificate validation",
        Justification = "Tests use an in-memory self-signed certificate over loopback.")]
    public static RemoteCertificateValidationCallback AcceptAnyCertificate() => (_, _, _, _) => true;

    public static X509Certificate2 CreateSelfSignedCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=nanomsg-sharp-test",
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

        // Re-import via PFX so the private key is persisted in a key store that SslStream's server
        // credentials accept on Windows (ephemeral keys from CreateSelfSigned are rejected there).
        byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
#endif
    }
}
