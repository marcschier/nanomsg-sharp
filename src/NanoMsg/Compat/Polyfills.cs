// Copyright (c) marcschier. Licensed under the MIT License.

#if NETSTANDARD2_0 || NETSTANDARD2_1
namespace NanoMsg.Compat;

/// <summary>
/// Runtime-API polyfills used only on the <c>netstandard2.0</c>/<c>netstandard2.1</c> builds. These are
/// compiled out of the net8/9/10 targets entirely (the whole file is under <c>#if NETSTANDARD…</c>), so
/// the modern builds are byte-identical to before and incur no overhead.
/// </summary>
internal static class Polyfills
{
    [ThreadStatic]
    private static Random? _random;

    /// <summary>A per-thread <see cref="Random"/> standing in for <c>Random.Shared</c> (net6+).</summary>
    public static Random SharedRandom => _random ??= new Random(Guid.NewGuid().GetHashCode());
}

/// <summary>Polyfills for the non-generic <c>TaskCompletionSource</c> (net5+) via the generic one.</summary>
internal static class TaskCompletionSourceExtensions
{
    public static void SetResult(this TaskCompletionSource<bool> source) => source.SetResult(true);

    public static bool TrySetResult(this TaskCompletionSource<bool> source) => source.TrySetResult(true);
}

/// <summary>Polyfill for <c>CancellationTokenSource.CancelAsync()</c> (net8+).</summary>
internal static class CancellationTokenSourceExtensions
{
    public static Task CancelAsync(this CancellationTokenSource source)
    {
        source.Cancel();
        return Task.CompletedTask;
    }
}
#endif
