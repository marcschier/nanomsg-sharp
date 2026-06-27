// Copyright (c) marcschier. Licensed under the MIT License.

using NanoMsg.Transports;

namespace NanoMsg.Tests.Transports;

public sealed class NanoAddressTests
{
    [Test]
    public async Task Parses_tcp_host_and_port()
    {
        NanoAddress address = NanoAddress.Parse("tcp://127.0.0.1:5555");
        await Assert.That(address.Scheme).IsEqualTo(AddressScheme.Tcp);
        await Assert.That(address.Host).IsEqualTo("127.0.0.1");
        await Assert.That(address.Port).IsEqualTo(5555);
        await Assert.That(address.IsWildcardHost).IsFalse();
    }

    [Test]
    public async Task Parses_tcp_bind_wildcard()
    {
        NanoAddress address = NanoAddress.Parse("tcp://*:5555");
        await Assert.That(address.IsWildcardHost).IsTrue();
        await Assert.That(address.Port).IsEqualTo(5555);
    }

    [Test]
    public async Task Parses_tcp_ipv6_literal()
    {
        NanoAddress address = NanoAddress.Parse("tcp://[::1]:9000");
        await Assert.That(address.Host).IsEqualTo("::1");
        await Assert.That(address.Port).IsEqualTo(9000);
    }

    [Test]
    public async Task Parses_tcp_with_local_interface()
    {
        NanoAddress address = NanoAddress.Parse("tcp://eth0;192.168.0.5:5555");
        await Assert.That(address.LocalInterface).IsEqualTo("eth0");
        await Assert.That(address.Host).IsEqualTo("192.168.0.5");
        await Assert.That(address.Port).IsEqualTo(5555);
    }

    [Test]
    public async Task Parses_ipc_path()
    {
        NanoAddress address = NanoAddress.Parse("ipc:///tmp/nanomsg.sock");
        await Assert.That(address.Scheme).IsEqualTo(AddressScheme.Ipc);
        await Assert.That(address.Path).IsEqualTo("/tmp/nanomsg.sock");
    }

    [Test]
    public async Task Parses_inproc_name()
    {
        NanoAddress address = NanoAddress.Parse("inproc://pipeline");
        await Assert.That(address.Scheme).IsEqualTo(AddressScheme.InProc);
        await Assert.That(address.Path).IsEqualTo("pipeline");
    }

    [Test]
    public async Task Parses_ws_with_explicit_path()
    {
        NanoAddress address = NanoAddress.Parse("ws://localhost:8080/bus");
        await Assert.That(address.Scheme).IsEqualTo(AddressScheme.Ws);
        await Assert.That(address.Host).IsEqualTo("localhost");
        await Assert.That(address.Port).IsEqualTo(8080);
        await Assert.That(address.Path).IsEqualTo("/bus");
    }

    [Test]
    public async Task Defaults_ws_path_to_root()
    {
        NanoAddress address = NanoAddress.Parse("ws://localhost:8080");
        await Assert.That(address.Path).IsEqualTo("/");
    }

    [Test]
    public async Task Allows_port_zero_for_ephemeral_bind()
    {
        NanoAddress address = NanoAddress.Parse("tcp://127.0.0.1:0");
        await Assert.That(address.Port).IsEqualTo(0);
    }

    [Test]
    public async Task Parses_tls_and_wss_schemes()
    {
        NanoAddress tls = NanoAddress.Parse("tls+tcp://127.0.0.1:6000");
        await Assert.That(tls.Scheme).IsEqualTo(AddressScheme.TlsTcp);
        await Assert.That(tls.Port).IsEqualTo(6000);

        NanoAddress wss = NanoAddress.Parse("wss://localhost:6001/bus");
        await Assert.That(wss.Scheme).IsEqualTo(AddressScheme.Wss);
        await Assert.That(wss.Host).IsEqualTo("localhost");
        await Assert.That(wss.Path).IsEqualTo("/bus");
    }

    [Test]
    public async Task Parses_udp_and_dtls_schemes()
    {
        NanoAddress udp = NanoAddress.Parse("udp://127.0.0.1:7000");
        await Assert.That(udp.Scheme).IsEqualTo(AddressScheme.Udp);
        await Assert.That(udp.Host).IsEqualTo("127.0.0.1");
        await Assert.That(udp.Port).IsEqualTo(7000);

        NanoAddress dtls = NanoAddress.Parse("dtls+udp://localhost:7001");
        await Assert.That(dtls.Scheme).IsEqualTo(AddressScheme.DtlsUdp);
        await Assert.That(dtls.Host).IsEqualTo("localhost");
        await Assert.That(dtls.Port).IsEqualTo(7001);
    }

    [Test]
    public async Task Parses_quic_scheme()
    {
        NanoAddress quic = NanoAddress.Parse("quic://127.0.0.1:8200");
        await Assert.That(quic.Scheme).IsEqualTo(AddressScheme.Quic);
        await Assert.That(quic.Host).IsEqualTo("127.0.0.1");
        await Assert.That(quic.Port).IsEqualTo(8200);
    }

    [Test]
    [Arguments("tcp4://127.0.0.1:5555", "tcp", "IPv4")]
    [Arguments("tcp6://[::1]:5555", "tcp", "IPv6")]
    [Arguments("tls+tcp4://127.0.0.1:5555", "tls+tcp", "IPv4")]
    [Arguments("ws6://[::1]:8080/x", "ws", "IPv6")]
    [Arguments("udp4://127.0.0.1:9000", "udp", "IPv4")]
    [Arguments("dtls+udp6://[::1]:9000", "dtls+udp", "IPv6")]
    [Arguments("quic4://127.0.0.1:9100", "quic", "IPv4")]
    [Arguments("quic6://[::1]:9100", "quic", "IPv6")]
    public async Task Parses_family_scheme_suffixes(string input, string baseScheme, string family)
    {
        NanoAddress address = NanoAddress.Parse(input);
        AddressScheme expectedScheme = baseScheme switch
        {
            "tcp" => AddressScheme.Tcp,
            "tls+tcp" => AddressScheme.TlsTcp,
            "ws" => AddressScheme.Ws,
            "udp" => AddressScheme.Udp,
            "dtls+udp" => AddressScheme.DtlsUdp,
            "quic" => AddressScheme.Quic,
            _ => throw new ArgumentOutOfRangeException(nameof(baseScheme)),
        };
        AddressFamilyPreference expectedFamily =
            family == "IPv4" ? AddressFamilyPreference.IPv4 : AddressFamilyPreference.IPv6;

        await Assert.That(address.Scheme).IsEqualTo(expectedScheme);
        await Assert.That(address.FamilyPreference).IsEqualTo(expectedFamily);
    }

    [Test]
    public async Task Default_scheme_has_unspecified_family()
    {
        NanoAddress address = NanoAddress.Parse("tcp://127.0.0.1:5555");
        await Assert.That(address.FamilyPreference).IsEqualTo(AddressFamilyPreference.Unspecified);
    }

    [Test]
    public async Task Rejects_malformed_addresses()
    {
        await Assert.That(NanoAddress.TryParse("nope://x:1", out _)).IsFalse();
        await Assert.That(NanoAddress.TryParse("tcp://host:70000", out _)).IsFalse();
        await Assert.That(NanoAddress.TryParse("tcp://host:notaport", out _)).IsFalse();
        await Assert.That(NanoAddress.TryParse("tcp://host", out _)).IsFalse();
        await Assert.That(NanoAddress.TryParse("nonsense", out _)).IsFalse();
        await Assert.That(NanoAddress.TryParse("inproc://", out _)).IsFalse();
    }

    [Test]
    public async Task Parse_throws_on_invalid_address()
    {
        bool threw = false;
        try
        {
            NanoAddress.Parse("bogus");
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
