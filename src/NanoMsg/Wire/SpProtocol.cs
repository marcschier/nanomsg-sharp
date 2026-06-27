// Copyright (c) marcschier. Licensed under the MIT License.

namespace NanoMsg.Wire;

/// <summary>
/// The 16-bit scalability-protocol identifiers exchanged in the SP connection header. Each value
/// encodes a protocol family in its upper bits and a role in its lower bits, matching the constants
/// used by the reference nanomsg implementation (for example PAIR = 16, PUB = 32, SUB = 33).
/// </summary>
internal enum SpProtocol : ushort
{
    /// <summary>One-to-one bidirectional pair (version 0; nanomsg-compatible, header-less).</summary>
    Pair = 16,

    /// <summary>NNG versioned pair (version 1): adds a 32-bit TTL header and optional polyamorous mode.</summary>
    Pair1 = 17,

    /// <summary>Publish endpoint of the publish/subscribe pattern.</summary>
    Pub = 32,

    /// <summary>Subscribe endpoint of the publish/subscribe pattern.</summary>
    Sub = 33,

    /// <summary>Request endpoint of the request/reply pattern.</summary>
    Req = 48,

    /// <summary>Reply endpoint of the request/reply pattern.</summary>
    Rep = 49,

    /// <summary>Push (fan-out) endpoint of the pipeline pattern.</summary>
    Push = 80,

    /// <summary>Pull (fan-in) endpoint of the pipeline pattern.</summary>
    Pull = 81,

    /// <summary>Surveyor endpoint of the survey pattern.</summary>
    Surveyor = 98,

    /// <summary>Respondent endpoint of the survey pattern.</summary>
    Respondent = 99,

    /// <summary>Many-to-many bus endpoint.</summary>
    Bus = 112,
}

/// <summary>Helpers for reasoning about <see cref="SpProtocol"/> values.</summary>
internal static class SpProtocols
{
    /// <summary>Gets the protocol family (the upper bits shared by both roles of a pattern).</summary>
    public static int Family(this SpProtocol protocol) => (int)protocol >> 4;

    /// <summary>
    /// Determines whether a local socket advertising <paramref name="self"/> may complete a
    /// connection with a peer advertising <paramref name="peer"/>. Compatibility is the exact
    /// counterpart relationship used by nanomsg, not merely a shared family.
    /// </summary>
    public static bool IsCompatibleWith(this SpProtocol self, SpProtocol peer) => self switch
    {
        SpProtocol.Pair => peer == SpProtocol.Pair,
        SpProtocol.Pair1 => peer == SpProtocol.Pair1,
        SpProtocol.Pub => peer == SpProtocol.Sub,
        SpProtocol.Sub => peer == SpProtocol.Pub,
        SpProtocol.Req => peer == SpProtocol.Rep,
        SpProtocol.Rep => peer == SpProtocol.Req,
        SpProtocol.Push => peer == SpProtocol.Pull,
        SpProtocol.Pull => peer == SpProtocol.Push,
        SpProtocol.Surveyor => peer == SpProtocol.Respondent,
        SpProtocol.Respondent => peer == SpProtocol.Surveyor,
        SpProtocol.Bus => peer == SpProtocol.Bus,
        _ => false,
    };

    /// <summary>Determines whether <paramref name="value"/> is one of the ten known protocols.</summary>
    public static bool IsDefined(SpProtocol value) => value switch
    {
        SpProtocol.Pair or SpProtocol.Pair1 or SpProtocol.Pub or SpProtocol.Sub or SpProtocol.Req
            or SpProtocol.Rep or SpProtocol.Push or SpProtocol.Pull or SpProtocol.Surveyor
            or SpProtocol.Respondent or SpProtocol.Bus => true,
        _ => false,
    };

    /// <summary>
    /// Gets the compatible counterpart protocol for <paramref name="self"/> (the protocol a peer must
    /// advertise to connect). Symmetric protocols (PAIR, PAIR1, BUS) are their own counterpart.
    /// </summary>
    public static SpProtocol Counterpart(this SpProtocol self) => self switch
    {
        SpProtocol.Pair => SpProtocol.Pair,
        SpProtocol.Pair1 => SpProtocol.Pair1,
        SpProtocol.Pub => SpProtocol.Sub,
        SpProtocol.Sub => SpProtocol.Pub,
        SpProtocol.Req => SpProtocol.Rep,
        SpProtocol.Rep => SpProtocol.Req,
        SpProtocol.Push => SpProtocol.Pull,
        SpProtocol.Pull => SpProtocol.Push,
        SpProtocol.Surveyor => SpProtocol.Respondent,
        SpProtocol.Respondent => SpProtocol.Surveyor,
        SpProtocol.Bus => SpProtocol.Bus,
        _ => throw new NanoMsgException($"Unknown protocol {self}."),
    };

    /// <summary>
    /// Gets the wire name used to build the WebSocket <c>Sec-WebSocket-Protocol</c> sub-protocol
    /// (<c>&lt;name&gt;.sp.nanomsg.org</c>), matching the names used by nanomsg and NNG.
    /// </summary>
    public static string WireName(this SpProtocol protocol) => protocol switch
    {
        SpProtocol.Pair => "pair",
        SpProtocol.Pair1 => "pair1",
        SpProtocol.Pub => "pub",
        SpProtocol.Sub => "sub",
        SpProtocol.Req => "req",
        SpProtocol.Rep => "rep",
        SpProtocol.Push => "push",
        SpProtocol.Pull => "pull",
        SpProtocol.Surveyor => "surveyor",
        SpProtocol.Respondent => "respondent",
        SpProtocol.Bus => "bus",
        _ => throw new NanoMsgException($"Unknown protocol {protocol}."),
    };
}
