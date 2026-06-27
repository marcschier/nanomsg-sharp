// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Dtls;
using NanoMsg.Wire;

namespace NanoMsg.Dtls;

/// <summary>
/// Negotiates the SP protocol over an established DTLS connection (which carries no sub-protocol of its
/// own) and maps <see cref="NanoSocketOptions"/> TLS settings onto DtlsSharp option objects.
/// </summary>
internal static class DtlsSpExchange
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Exchanges 8-byte SP headers over <paramref name="connection"/> and returns the peer's protocol.
    /// The local header is retransmitted (DTLS does not retransmit application data) until the peer's
    /// header arrives or the deadline elapses.
    /// </summary>
    /// <param name="connection">The established DTLS connection.</param>
    /// <param name="local">The local SP protocol.</param>
    /// <param name="cancellationToken">A token used to cancel the exchange.</param>
    /// <returns>The peer's advertised SP protocol.</returns>
    public static async Task<SpProtocol> PerformAsync(
        DtlsConnection connection,
        SpProtocol local,
        CancellationToken cancellationToken)
    {
        byte[] outbound = new byte[SpHeader.Size];
        new SpHeader(local).WriteTo(outbound);
        byte[] inbound = new byte[SpHeader.Size + 32];

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            while (true)
            {
                await connection.SendAsync(outbound, deadline.Token).ConfigureAwait(false);

                using CancellationTokenSource attempt =
                    CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(RetryInterval);
                try
                {
                    while (true)
                    {
                        int count = await connection.ReceiveAsync(inbound, attempt.Token).ConfigureAwait(false);
                        if (count >= SpHeader.Size && SpHeader.TryParse(inbound.AsSpan(0, count), out SpHeader header))
                        {
                            return header.Protocol;
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (attempt.IsCancellationRequested && !deadline.IsCancellationRequested)
                {
                    // Retry window elapsed; resend the SP header.
                }
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new NanoMsgException("Timed out negotiating the SP protocol over DTLS.");
        }
    }

    /// <summary>Builds <see cref="DtlsClientOptions"/> from <paramref name="options"/> for a dialer.</summary>
    /// <param name="options">The socket options.</param>
    /// <param name="targetHost">The address host (default SNI / validation name).</param>
    /// <returns>The mapped client options.</returns>
    public static DtlsClientOptions BuildClientOptions(NanoSocketOptions options, string targetHost)
    {
        DtlsClientOptions client = new()
        {
            TargetHost = options.TlsTargetHost ?? targetHost,
            RemoteCertificateValidation = Adapt(options.TlsRemoteValidationCallback),
        };

        if (options.TlsClientCertificates is not null)
        {
            foreach (X509Certificate certificate in options.TlsClientCertificates)
            {
                if (certificate is X509Certificate2 certificate2)
                {
                    client.ClientCertificates.Add(certificate2);
                }
            }
        }

        return client;
    }

    /// <summary>Builds <see cref="DtlsServerOptions"/> from <paramref name="options"/> for a listener.</summary>
    /// <param name="options">The socket options (must carry a server certificate).</param>
    /// <returns>The mapped server options.</returns>
    public static DtlsServerOptions BuildServerOptions(NanoSocketOptions options) => new()
    {
        ServerCertificate = options.TlsServerCertificate,
    };

    private static readonly object ValidationSender = new();

    private static DtlsRemoteCertificateValidation? Adapt(RemoteCertificateValidationCallback? callback)
    {
        if (callback is null)
        {
            return null;
        }

        return (certificate, chain, nameValidationFailed) => callback(
            ValidationSender,
            certificate,
            chain,
            nameValidationFailed ? SslPolicyErrors.RemoteCertificateNameMismatch : SslPolicyErrors.None);
    }
}
