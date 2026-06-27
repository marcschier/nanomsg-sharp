// Copyright (c) marcschier. Licensed under the MIT License.

#if NET6_0_OR_GREATER

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace NanoMsg.Transports;

/// <summary>
/// Performs the server side of the RFC 6455 WebSocket opening handshake over an already-connected
/// (and, for <c>wss</c>, already-TLS-secured) <see cref="Stream"/>. It validates the HTTP
/// <c>Upgrade</c> request, requires the SP sub-protocol, writes the <c>101 Switching Protocols</c>
/// response, and returns a server <see cref="WebSocket"/> bound to the stream. The client side is
/// handled by <see cref="ClientWebSocket"/>.
/// </summary>
internal static class WsHandshake
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const string AcceptMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Completes the server handshake and returns the negotiated WebSocket.</summary>
    /// <param name="stream">The connected stream (TCP, or TLS for <c>wss</c>).</param>
    /// <param name="subProtocol">The required <c>Sec-WebSocket-Protocol</c> value to negotiate.</param>
    /// <param name="cancellationToken">A token used to cancel the handshake.</param>
    /// <returns>A server-mode <see cref="WebSocket"/> over <paramref name="stream"/>.</returns>
    /// <exception cref="NanoMsgException">The request was not a valid, matching WebSocket upgrade.</exception>
    public static async ValueTask<WebSocket> AcceptAsync(
        Stream stream,
        string subProtocol,
        CancellationToken cancellationToken)
    {
        string request = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> headers = ParseHeaders(request);

        if (!HeaderContains(headers, "Upgrade", "websocket") ||
            !headers.TryGetValue("sec-websocket-key", out string? key) ||
            string.IsNullOrWhiteSpace(key))
        {
            await WriteErrorAsync(stream, "400 Bad Request", cancellationToken).ConfigureAwait(false);
            throw new NanoMsgException("Inbound request is not a valid WebSocket upgrade.");
        }

        if (!HeaderContains(headers, "Sec-WebSocket-Protocol", subProtocol))
        {
            await WriteErrorAsync(stream, "400 Bad Request", cancellationToken).ConfigureAwait(false);
            throw new NanoMsgException(
                $"WebSocket client did not request the '{subProtocol}' sub-protocol (incompatible SP protocol).");
        }

        string accept = ComputeAcceptKey(key.Trim());
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            $"Sec-WebSocket-Protocol: {subProtocol}\r\n\r\n";
        byte[] responseBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return WebSocket.CreateFromStream(
            stream,
            new WebSocketCreationOptions
            {
                IsServer = true,
                SubProtocol = subProtocol,
                KeepAliveInterval = TimeSpan.Zero,
            });
    }

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is mandated by RFC 6455 for the WebSocket accept key; it is not used for security.")]
    private static string ComputeAcceptKey(string key)
    {
        byte[] hash = SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptMagic));
        return Convert.ToBase64String(hash);
    }

    private static async ValueTask<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int total = 0;
            while (true)
            {
                if (total == buffer.Length)
                {
                    buffer = Grow(buffer, total);
                }

                int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new NanoMsgException("Connection closed during the WebSocket handshake.");
                }

                total += read;
                int end = FindHeaderEnd(buffer.AsSpan(0, total));
                if (end >= 0)
                {
                    return Encoding.ASCII.GetString(buffer, 0, end);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] Grow(byte[] buffer, int length)
    {
        if (buffer.Length >= MaxHeaderBytes)
        {
            throw new NanoMsgException("WebSocket request headers exceeded the maximum size.");
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxHeaderBytes));
        Array.Copy(buffer, bigger, length);
        ArrayPool<byte>.Shared.Return(buffer);
        return bigger;
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> buffer)
    {
        for (int i = 3; i < buffer.Length; i++)
        {
            if (buffer[i] == '\n' && buffer[i - 1] == '\r' && buffer[i - 2] == '\n' && buffer[i - 3] == '\r')
            {
                return i - 3;
            }
        }

        return -1;
    }

    private static Dictionary<string, string> ParseHeaders(string request)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        string[] lines = request.Split("\r\n");
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string name = lines[i].Substring(0, colon).Trim();
            string value = lines[i].Substring(colon + 1).Trim();
            headers[name] = headers.TryGetValue(name, out string? existing) ? $"{existing},{value}" : value;
        }

        return headers;
    }

    private static bool HeaderContains(Dictionary<string, string> headers, string name, string token)
    {
        if (!headers.TryGetValue(name, out string? value))
        {
            return false;
        }

        foreach (string part in value.Split(','))
        {
            if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask WriteErrorAsync(Stream stream, string status, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}
#endif // NET6_0_OR_GREATER
