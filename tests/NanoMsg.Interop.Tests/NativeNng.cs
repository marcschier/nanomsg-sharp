// Copyright (c) marcschier. Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace NanoMsg.Interop.Tests;

/// <summary>
/// P/Invoke surface for the reference C <c>libnng</c> (nanomsg-next-gen), used by the interop tests to
/// drive a real NNG peer on the other end of a NanoMsgSharp socket. NNG shares the SP wire protocol
/// with nanomsg but exposes a very different C API (<c>nng_*</c>). The library is referenced only at
/// runtime and only by this test project; the tests skip when it cannot be loaded (for example on a
/// Windows dev box without <c>libnng</c>), and run for real on the Linux CI runner where it is installed.
/// </summary>
internal static class NativeNng
{
    private const string Library = "nng";

    // NNG option names (see nng/nng.h).
    private const string OptRecvTimeout = "recv-timeout";
    private const string OptSendTimeout = "send-timeout";
    private const string OptSubSubscribe = "sub:subscribe";
    private const string OptSurveyorSurveyTime = "surveyor:survey-time";

    /// <summary>Gets a value indicating whether the native NNG library can be loaded.</summary>
    public static bool IsAvailable { get; } = TryProbe();

    /// <summary>A protocol constructor (one of the <c>nng_*_open</c> functions).</summary>
    /// <param name="socket">Receives the opened socket.</param>
    /// <returns>0 on success.</returns>
    public delegate int OpenFunc(out NngSocket socket);

    /// <summary>Opens a pair (v0) socket.</summary>
    public static NngSocket OpenPair0() => Open(nng_pair0_open);

    /// <summary>Opens a pair (v1) socket.</summary>
    public static NngSocket OpenPair1() => Open(nng_pair1_open);

    /// <summary>Opens a publish socket.</summary>
    public static NngSocket OpenPub0() => Open(nng_pub0_open);

    /// <summary>Opens a subscribe socket.</summary>
    public static NngSocket OpenSub0() => Open(nng_sub0_open);

    /// <summary>Opens a request socket.</summary>
    public static NngSocket OpenReq0() => Open(nng_req0_open);

    /// <summary>Opens a reply socket.</summary>
    public static NngSocket OpenRep0() => Open(nng_rep0_open);

    /// <summary>Opens a push socket.</summary>
    public static NngSocket OpenPush0() => Open(nng_push0_open);

    /// <summary>Opens a pull socket.</summary>
    public static NngSocket OpenPull0() => Open(nng_pull0_open);

    /// <summary>Opens a surveyor socket.</summary>
    public static NngSocket OpenSurveyor0() => Open(nng_surveyor0_open);

    /// <summary>Opens a respondent socket.</summary>
    public static NngSocket OpenRespondent0() => Open(nng_respondent0_open);

    /// <summary>Opens a bus socket.</summary>
    public static NngSocket OpenBus0() => Open(nng_bus0_open);

    /// <summary>Opens a socket via <paramref name="open"/> and applies default send/receive timeouts.</summary>
    /// <param name="open">The protocol constructor.</param>
    /// <returns>The opened socket.</returns>
    public static NngSocket Open(OpenFunc open)
    {
        int rv = open(out NngSocket socket);
        if (rv != 0)
        {
            throw new InvalidOperationException($"nng open failed: {ErrorString(rv)}");
        }

        SetMs(socket, OptRecvTimeout, 4000);
        SetMs(socket, OptSendTimeout, 4000);
        return socket;
    }

    /// <summary>Binds (listens) the socket at <paramref name="address"/>.</summary>
    /// <param name="socket">The socket.</param>
    /// <param name="address">The endpoint URL.</param>
    public static void Listen(NngSocket socket, string address)
    {
        int rv = nng_listen(socket, address, IntPtr.Zero, 0);
        if (rv != 0)
        {
            throw new InvalidOperationException($"nng_listen('{address}') failed: {ErrorString(rv)}");
        }
    }

    /// <summary>Connects (dials) the socket to <paramref name="address"/>.</summary>
    /// <param name="socket">The socket.</param>
    /// <param name="address">The endpoint URL.</param>
    public static void Dial(NngSocket socket, string address)
    {
        int rv = nng_dial(socket, address, IntPtr.Zero, 0);
        if (rv != 0)
        {
            throw new InvalidOperationException($"nng_dial('{address}') failed: {ErrorString(rv)}");
        }
    }

    /// <summary>Sends <paramref name="data"/> on the socket.</summary>
    /// <param name="socket">The socket.</param>
    /// <param name="data">The payload.</param>
    public static void Send(NngSocket socket, byte[] data)
    {
        int rv = nng_send(socket, data, (nuint)data.Length, 0);
        if (rv != 0)
        {
            throw new InvalidOperationException($"nng_send failed: {ErrorString(rv)}");
        }
    }

    /// <summary>Receives a message into a preallocated buffer, or returns <see langword="null"/> on error.</summary>
    /// <param name="socket">The socket.</param>
    /// <returns>The received bytes, or <see langword="null"/>.</returns>
    public static byte[]? Receive(NngSocket socket)
    {
        byte[] buffer = new byte[65536];
        nuint size = (nuint)buffer.Length;
        int rv = nng_recv(socket, buffer, ref size, 0);
        return rv != 0 ? null : buffer[..(int)size];
    }

    /// <summary>Registers a SUB subscription prefix.</summary>
    /// <param name="socket">The subscribe socket.</param>
    /// <param name="prefix">The subscription prefix.</param>
    public static void Subscribe(NngSocket socket, byte[] prefix) =>
        _ = nng_socket_set(socket, OptSubSubscribe, prefix, (nuint)prefix.Length);

    /// <summary>Sets the surveyor survey time in milliseconds.</summary>
    /// <param name="socket">The surveyor socket.</param>
    /// <param name="milliseconds">The deadline.</param>
    public static void SetSurveyorTime(NngSocket socket, int milliseconds) =>
        SetMs(socket, OptSurveyorSurveyTime, milliseconds);

    /// <summary>Closes the socket.</summary>
    /// <param name="socket">The socket.</param>
    public static void Close(NngSocket socket) => _ = nng_close(socket);

    private static void SetMs(NngSocket socket, string option, int milliseconds) =>
        _ = nng_socket_set_ms(socket, option, milliseconds);

    private static string ErrorString(int code) =>
        Marshal.PtrToStringAnsi(nng_strerror(code)) ?? $"error {code}";

    private static bool TryProbe()
    {
        try
        {
            return NativeLibrary.TryLoad(Library, out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>The NNG socket handle (a 32-bit id wrapped in a struct, matching <c>nng_socket</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NngSocket
    {
        /// <summary>The socket id.</summary>
        public uint Id;
    }

#pragma warning disable SYSLIB1054 // DllImport is sufficient for this test-only interop surface.
    [DllImport(Library)]
    private static extern int nng_pair0_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_pair1_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_pub0_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_sub0_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_req0_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_rep0_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_push0_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_pull0_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_surveyor0_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_respondent0_open(out NngSocket socket);

    [DllImport(Library)]
    private static extern int nng_bus0_open(out NngSocket socket);

    [DllImport(Library, CharSet = CharSet.Ansi)]
    private static extern int nng_listen(NngSocket socket, string url, IntPtr listener, int flags);

    [DllImport(Library, CharSet = CharSet.Ansi)]
    private static extern int nng_dial(NngSocket socket, string url, IntPtr dialer, int flags);

    [DllImport(Library)]
    private static extern int nng_send(NngSocket socket, byte[] data, nuint size, int flags);

    [DllImport(Library)]
    private static extern int nng_recv(NngSocket socket, byte[] data, ref nuint size, int flags);

    [DllImport(Library, CharSet = CharSet.Ansi)]
    private static extern int nng_socket_set(NngSocket socket, string option, byte[] value, nuint size);

    [DllImport(Library, CharSet = CharSet.Ansi)]
    private static extern int nng_socket_set_ms(NngSocket socket, string option, int milliseconds);

    [DllImport(Library)]
    private static extern int nng_close(NngSocket socket);

    [DllImport(Library)]
    private static extern IntPtr nng_strerror(int code);
#pragma warning restore SYSLIB1054
}
