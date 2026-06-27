// Copyright (c) marcschier. Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace NanoMsg.Interop.Tests;

/// <summary>
/// P/Invoke surface for the reference C <c>libnanomsg</c>, used by the interop tests to drive a real
/// nanomsg peer on the other end of a NanoMsgSharp socket. The library is referenced only at runtime
/// and only by this test project; the tests skip when it cannot be loaded.
/// </summary>
internal static class NativeNanoMsg
{
    private const string Library = "nanomsg";

    /// <summary>The SP address family.</summary>
    public const int AfSp = 1;

    /// <summary>One-to-one pair protocol.</summary>
    public const int Pair = 16;

    /// <summary>Publish protocol.</summary>
    public const int Pub = 32;

    /// <summary>Subscribe protocol.</summary>
    public const int Sub = 33;

    /// <summary>Request protocol.</summary>
    public const int Req = 48;

    /// <summary>Reply protocol.</summary>
    public const int Rep = 49;

    /// <summary>Push (fan-out) protocol.</summary>
    public const int Push = 80;

    /// <summary>Pull (fan-in) protocol.</summary>
    public const int Pull = 81;

    /// <summary>Surveyor protocol.</summary>
    public const int Surveyor = 98;

    /// <summary>Respondent protocol.</summary>
    public const int Respondent = 99;

    /// <summary>Bus protocol.</summary>
    public const int Bus = 112;

    private const int SolSocket = 0;
    private const int OptionSendTimeout = 4;
    private const int OptionReceiveTimeout = 5;
    private const int SubSubscribe = 1;
    private const int SurveyorDeadline = 1;

    /// <summary>Gets a value indicating whether the native nanomsg library can be loaded.</summary>
    public static bool IsAvailable { get; } = TryProbe();

    /// <summary>Creates an SP socket of the given protocol with sensible send/receive timeouts.</summary>
    /// <param name="protocol">The protocol number.</param>
    /// <returns>The native socket handle.</returns>
    public static int CreateSocket(int protocol)
    {
        int socket = nn_socket(AfSp, protocol);
        if (socket < 0)
        {
            throw new InvalidOperationException($"nn_socket failed: {LastError()}");
        }

        SetTimeout(socket, OptionReceiveTimeout, 4000);
        SetTimeout(socket, OptionSendTimeout, 4000);
        return socket;
    }

    /// <summary>Binds the socket to <paramref name="address"/>.</summary>
    /// <param name="socket">The native socket.</param>
    /// <param name="address">The endpoint address.</param>
    public static void Bind(int socket, string address)
    {
        if (nn_bind(socket, address) < 0)
        {
            throw new InvalidOperationException($"nn_bind('{address}') failed: {LastError()}");
        }
    }

    /// <summary>Connects the socket to <paramref name="address"/>.</summary>
    /// <param name="socket">The native socket.</param>
    /// <param name="address">The endpoint address.</param>
    public static void Connect(int socket, string address)
    {
        if (nn_connect(socket, address) < 0)
        {
            throw new InvalidOperationException($"nn_connect('{address}') failed: {LastError()}");
        }
    }

    /// <summary>Sends <paramref name="data"/> on the socket.</summary>
    /// <param name="socket">The native socket.</param>
    /// <param name="data">The payload.</param>
    public static void Send(int socket, byte[] data)
    {
        int sent = nn_send(socket, data, (nuint)data.Length, 0);
        if (sent < 0)
        {
            throw new InvalidOperationException($"nn_send failed: {LastError()}");
        }
    }

    /// <summary>Receives a message, or returns <see langword="null"/> on timeout.</summary>
    /// <param name="socket">The native socket.</param>
    /// <returns>The received bytes, or <see langword="null"/>.</returns>
    public static byte[]? Receive(int socket)
    {
        byte[] buffer = new byte[65536];
        int received = nn_recv(socket, buffer, (nuint)buffer.Length, 0);
        return received < 0 ? null : buffer[..received];
    }

    /// <summary>Registers a SUB subscription prefix.</summary>
    /// <param name="socket">The native subscribe socket.</param>
    /// <param name="prefix">The subscription prefix.</param>
    public static void Subscribe(int socket, byte[] prefix) =>
        _ = nn_setsockopt(socket, Sub, SubSubscribe, prefix, (nuint)prefix.Length);

    /// <summary>Sets the surveyor deadline in milliseconds.</summary>
    /// <param name="socket">The native surveyor socket.</param>
    /// <param name="milliseconds">The deadline.</param>
    public static void SetSurveyorDeadline(int socket, int milliseconds)
    {
        int value = milliseconds;
        _ = nn_setsockopt(socket, Surveyor, SurveyorDeadline, ref value, sizeof(int));
    }

    /// <summary>Closes the socket.</summary>
    /// <param name="socket">The native socket.</param>
    public static void Close(int socket) => _ = nn_close(socket);

    private static void SetTimeout(int socket, int option, int milliseconds)
    {
        int value = milliseconds;
        _ = nn_setsockopt(socket, SolSocket, option, ref value, sizeof(int));
    }

    private static string LastError() => Marshal.PtrToStringAnsi(nn_strerror(nn_errno())) ?? "unknown error";

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

#pragma warning disable SYSLIB1054 // DllImport is sufficient for this test-only interop surface.
    [DllImport(Library)]
    private static extern int nn_socket(int domain, int protocol);

    [DllImport(Library, CharSet = CharSet.Ansi)]
    private static extern int nn_bind(int socket, string address);

    [DllImport(Library, CharSet = CharSet.Ansi)]
    private static extern int nn_connect(int socket, string address);

    [DllImport(Library)]
    private static extern int nn_send(int socket, byte[] buffer, nuint length, int flags);

    [DllImport(Library)]
    private static extern int nn_recv(int socket, byte[] buffer, nuint length, int flags);

    [DllImport(Library)]
    private static extern int nn_setsockopt(int socket, int level, int option, byte[] value, nuint length);

    [DllImport(Library)]
    private static extern int nn_setsockopt(int socket, int level, int option, ref int value, nuint length);

    [DllImport(Library)]
    private static extern int nn_close(int socket);

    [DllImport(Library)]
    private static extern int nn_errno();

    [DllImport(Library)]
    private static extern IntPtr nn_strerror(int errnum);
#pragma warning restore SYSLIB1054
}
