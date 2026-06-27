// Copyright (c) marcschier. Licensed under the MIT License.

using Dtls;
using Dtls.Transport;
using NanoMsg.Transports;

namespace NanoMsg.Dtls;

/// <summary>
/// An <see cref="IDatagramChannel"/> over an established <see cref="DtlsConnection"/>: every SP message
/// is sent and received as one confidential, authenticated DTLS application datagram. The owned DTLS
/// connection and its underlying datagram transport are disposed together with this channel.
/// </summary>
internal sealed class DtlsDatagramChannel : IDatagramChannel
{
    private readonly DtlsConnection _connection;
    private readonly IDatagramTransport _transport;
    private readonly byte[] _receiveBuffer = new byte[UdpDatagramTransport.MaxUdpPayload];
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="DtlsDatagramChannel"/> class.</summary>
    /// <param name="connection">The established DTLS connection.</param>
    /// <param name="transport">The owned underlying datagram transport.</param>
    public DtlsDatagramChannel(DtlsConnection connection, IDatagramTransport transport)
    {
        _connection = connection;
        _transport = transport;
    }

    /// <inheritdoc/>
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        _connection.SendAsync(message, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            int count = await _connection.ReceiveAsync(_receiveBuffer, cancellationToken).ConfigureAwait(false);
            return count <= 0 ? null : _receiveBuffer.AsMemory(0, count);
        }
        catch (Exception ex) when (ex is DtlsException or IOException or ObjectDisposedException
            or OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _connection.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DtlsException or IOException or ObjectDisposedException
            or OperationCanceledException)
        {
        }

        _connection.Dispose();
        _transport.Dispose();
    }
}
