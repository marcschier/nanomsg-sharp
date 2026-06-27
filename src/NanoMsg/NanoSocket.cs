// Copyright (c) marcschier. Licensed under the MIT License.

using NanoMsg.Protocols;

namespace NanoMsg;

/// <summary>
/// The base class for every scalability-protocol socket. Provides the endpoint operations common to
/// all patterns — <see cref="BindAsync"/> (listen), <see cref="Connect"/> (dial with automatic
/// reconnect), and asynchronous disposal. A socket may bind and/or connect any number of endpoints.
/// </summary>
public abstract class NanoSocket : IAsyncDisposable
{
    private readonly NanoSocketCore _core;

    private protected NanoSocket(NanoSocketCore core) => _core = core;

    private protected NanoSocketCore Core => _core;

    /// <summary>Binds a local endpoint and begins accepting peers.</summary>
    /// <param name="address">The endpoint address (for example <c>tcp://*:5555</c> or <c>inproc://name</c>).</param>
    /// <param name="cancellationToken">A token used to cancel the bind.</param>
    /// <returns>The resolved local port for <c>tcp</c> endpoints (0 for other transports).</returns>
    public ValueTask<int> BindAsync(string address, CancellationToken cancellationToken = default) =>
        _core.BindAsync(address, cancellationToken);

    /// <summary>Begins connecting (and reconnecting) to a remote endpoint in the background.</summary>
    /// <param name="address">The endpoint address.</param>
    public void Connect(string address) => _core.Connect(address);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _core.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Waits until at least <paramref name="count"/> peers are connected (test/diagnostic helper).</summary>
    internal ValueTask WaitForConnectionsAsync(
        int count,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        _core.WaitForPipesAsync(count, timeout, cancellationToken);
}
