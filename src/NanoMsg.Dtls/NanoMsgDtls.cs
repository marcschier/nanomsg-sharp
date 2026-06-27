// Copyright (c) marcschier. Licensed under the MIT License.

using System.Runtime.CompilerServices;
using NanoMsg.Transports;

namespace NanoMsg.Dtls;

/// <summary>
/// Registers the DTLS-over-UDP transport (<c>dtls+udp://</c>) with NanoMsgSharp. Registration happens
/// automatically when this assembly is loaded (via a module initializer); call <see cref="Register"/>
/// explicitly if a trimmer or NativeAOT might otherwise drop the initializer.
/// </summary>
public static class NanoMsgDtls
{
    private static int _registered;

    /// <summary>
    /// Registers the <c>dtls+udp</c> transport so <c>dtls+udp://</c> endpoints can be bound and dialed.
    /// Idempotent and safe to call multiple times.
    /// </summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            TransportFactory.Register(AddressScheme.DtlsUdp, static () => new DtlsUdpTransport());
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Intentional, idempotent auto-registration of the dtls+udp transport on load; " +
            "Register() is also exposed for trim/AOT scenarios.")]
    [ModuleInitializer]
    internal static void Initialize() => Register();
}
