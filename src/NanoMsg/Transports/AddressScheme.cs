// Copyright (c) marcschier. Licensed under the MIT License.

namespace NanoMsg.Transports;

/// <summary>The transport schemes supported by an endpoint address.</summary>
internal enum AddressScheme
{
    /// <summary>In-process transport (<c>inproc://</c>).</summary>
    InProc,

    /// <summary>TCP transport (<c>tcp://</c>).</summary>
    Tcp,

    /// <summary>TLS-over-TCP transport (<c>tls+tcp://</c>).</summary>
    TlsTcp,

    /// <summary>Inter-process transport (<c>ipc://</c>): Unix domain socket or Windows named pipe.</summary>
    Ipc,

    /// <summary>WebSocket transport (<c>ws://</c>).</summary>
    Ws,

    /// <summary>Secure WebSocket transport (<c>wss://</c>).</summary>
    Wss,

    /// <summary>UDP datagram transport (<c>udp://</c>); provided by the NanoMsgSharp.Extensions package.</summary>
    Udp,

    /// <summary>DTLS-over-UDP datagram transport (<c>dtls+udp://</c>); provided by NanoMsgSharp.Extensions.</summary>
    DtlsUdp,

    /// <summary>QUIC transport (<c>quic://</c>): SP over a bidirectional QUIC stream (.NET 8+).</summary>
    Quic,
}

/// <summary>An optional IPv4/IPv6 restriction parsed from a scheme suffix (such as <c>tcp4</c>/<c>tcp6</c>).</summary>
internal enum AddressFamilyPreference
{
    /// <summary>No restriction; the platform default family is used.</summary>
    Unspecified,

    /// <summary>Restrict to IPv4 (the <c>4</c> scheme suffix).</summary>
    IPv4,

    /// <summary>Restrict to IPv6 (the <c>6</c> scheme suffix).</summary>
    IPv6,
}
