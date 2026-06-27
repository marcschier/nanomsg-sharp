// Copyright (c) marcschier. Licensed under the MIT License.

#if NETSTANDARD2_0
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace NanoMsg.Compat;

/// <summary>Polyfill for <c>Stream.DisposeAsync()</c> (netstandard2.1+/net), via synchronous dispose.</summary>
internal static class StreamPolyfills
{
    public static ValueTask DisposeAsync(this Stream stream)
    {
        stream.Dispose();
        return default;
    }
}

/// <summary>Polyfill for <c>ChannelReader&lt;T&gt;.ReadAllAsync(CancellationToken)</c> (net5+).</summary>
internal static class ChannelReaderPolyfills
{
    public static async IAsyncEnumerable<T> ReadAllAsync<T>(
        this ChannelReader<T> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out T? item))
            {
                yield return item;
            }
        }
    }
}
#endif
