# Wire format

NanoMsgSharp speaks the **Scalability Protocols** (SP) wire format shared by nanomsg and NNG (nanomsg-next-gen), so a .NET socket interoperates with both a C `libnanomsg` peer and a C `libnng` peer. The constants and layouts below are validated by the interop tests against `libnanomsg` 1.1.5 and `libnng` 1.5.2.

## SP connection header

Exchanged once per connection over the stream transports (`tcp`, `tls+tcp`, `ipc`), immediately after the transport connects, by each side:

```
byte:  0    1    2    3    4    5    6    7
       0x00 'S'  'P'  0x00 ── protocol ──  0x00 0x00
                            (uint16, BE)   (reserved)
```

The 16-bit protocol number identifies the sender's role. A connection proceeds only if the two advertised protocols are compatible counterparts. (Over WebSocket the header is **not** sent on the wire; the protocol is negotiated by the sub-protocol — see below.)

| Protocol | Number | Counterpart |
| --- | --- | --- |
| PAIR (v0) | 16 | PAIR |
| PAIR1 | 17 | PAIR1 |
| PUB | 32 | SUB |
| SUB | 33 | PUB |
| REQ | 48 | REP |
| REP | 49 | REQ |
| PUSH | 80 | PULL |
| PULL | 81 | PUSH |
| SURVEYOR | 98 | RESPONDENT |
| RESPONDENT | 99 | SURVEYOR |
| BUS | 112 | BUS |

## Message framing (tcp, tls+tcp, ipc)

After the handshake, every message is a length-prefixed frame:

```
┌──────────────────────────┬───────────────────────┐
│ length (uint64, BE)      │ body (length bytes)   │
└──────────────────────────┴───────────────────────┘
```

The length covers the body only (the 8-byte prefix is not counted). `tls+tcp` is identical, wrapped in a TLS (`SslStream`) session.

## Per-protocol body headers

Most protocols put the application payload directly in the body. REQ/REP and SURVEY prepend a 4-byte big-endian id (the most-significant bit is set) so replies and responses can be correlated:

```
REQ → REP:        [ request id (uint32, BE, MSB set) ][ payload ]
REP → REQ:        [ request id ][ reply ]              (id echoed back)
SURVEYOR → RESP:  [ survey id  (uint32, BE, MSB set) ][ payload ]
RESP → SURVEYOR:  [ survey id  ][ response ]           (id echoed back)
```

PAIR (v0), PUB/SUB, PUSH/PULL, and BUS carry the raw payload with no per-message header. PUB/SUB filtering is performed entirely on the SUB side (no subscription messages travel on the wire), matching the reference nanomsg behaviour.

### PAIR1 (NNG)

NNG's versioned PAIR adds a single 4-byte big-endian header to every message whose low byte is a hop count (TTL), initialized to `1` and incremented by one per device hop for loop protection. PAIR1 also supports an optional *polyamorous* mode (one socket, many peers, with directed send back to the peer a message arrived from). PAIR1 is the only protocol introduced by NNG; PAIR (v0) remains the header-less, nanomsg-compatible default.

```
PAIR1:  [ ttl/hop-count (uint32, BE; low byte = hops) ][ payload ]
```

## SP-over-WebSocket (ws, wss)

The WebSocket transport follows the [SP-over-WebSocket mapping](https://github.com/nanomsg/nanomsg/blob/master/rfc/sp-websocket-mapping-01.txt) used by nanomsg and NNG, which differs from the stream transports:

- **Protocol negotiation** uses the `Sec-WebSocket-Protocol` HTTP header rather than the 8-byte SP header. The value is `<name>.sp.nanomsg.org`, where `<name>` is the protocol name (`pair`, `pair1`, `pub`, `sub`, `req`, `rep`, `push`, `pull`, `surveyor`, `respondent`, `bus`). The client offers the server's protocol name (its own counterpart); the server echoes its own protocol name — they match for a compatible pair.
- **Framing**: each SP message maps directly to exactly one binary WebSocket message; there is **no** 8-byte length prefix (the WebSocket framing already delimits the message). Any per-protocol body header (REQ/REP, SURVEY, PAIR1) is carried as the leading bytes of that message.
- `wss` is the same mapping over a TLS (`SslStream`) session.

## SP-over-UDP (udp, dtls+udp)

The UDP transport (`udp://`, core package) mirrors NNG's *experimental* udp transport: it is unreliable and unordered, and maps **each SP message to exactly one UDP packet** (≤ 65000 bytes, no 8-byte length prefix). A per-peer logical connection is established by a small handshake before data flows. Every datagram carries a fixed 20-byte transport header (modelled on NNG's `udp_sp_msg`):

```
byte:  0     1       2..3        4..5         6..7        8..11       12..15      16..19
       ver   opcode  SP proto    param0       param1      sender id   peer id     reserved
       (1)   (0..3)  (uint16,BE) (uint16,BE)  (uint16,BE) (uint32,BE) (uint32,BE) (zero)
```

- `opcode`: `0` DATA (payload is one SP message), `1` CREQ (connection request, from the dialer), `2` CACK (connection ack, from the listener), `3` DISC (disconnect).
- `param0`: DATA payload length / CREQ-CACK advertised receive-max / DISC reason. `param1`: CREQ-CACK refresh interval (seconds).
- The dialer sends CREQ (retransmitting until a CACK arrives); the listener replies CACK and demultiplexes subsequent datagrams by remote endpoint. The SP protocol is exchanged in the CREQ/CACK `SP proto` field, so the 8-byte SP header is not sent over UDP.

> NanoMsgSharp's udp framing is modelled on NNG's experimental transport but is **not** wire-verified against native NNG (the udp transport is absent from released `libnng` packages, and NNG documents its wire format as subject to change).

The `dtls+udp://` transport (the optional `NanoMsgSharp.Dtls` package) carries each SP message as one DTLS application datagram over the same per-packet model, using [DtlsSharp](https://www.nuget.org/packages/DtlsSharp) for the DTLS 1.2/1.3 handshake and record protection. After the DTLS handshake, the two peers exchange their 8-byte SP headers over the secure channel to negotiate the protocol, then send DATA datagrams.

## Transports

| Scheme | Mapping |
| --- | --- |
| `inproc://name` | in-process pipe pair (no serialization beyond the in-memory framing) |
| `tcp://host:port` | TCP; `host` may be `*` (bind wildcard); port 0 selects an ephemeral port |
| `tls+tcp://host:port` | TLS over TCP (`SslStream`); requires a server certificate to bind |
| `ipc://path` | Unix domain socket (Unix) or named pipe `\\.\pipe\<path>` (Windows) |
| `ws://host:port/path` | WebSocket, one binary message per SP message |
| `wss://host:port/path` | WebSocket over TLS |
| `udp://host:port` | UDP datagram, one SP message per packet (experimental) |
| `dtls+udp://host:port` | DTLS-secured UDP datagram (`NanoMsgSharp.Dtls` package) |

Each scheme also accepts a `4`/`6` address-family suffix (`tcp4`, `udp6`, `dtls+udp4`, …) to force IPv4 or IPv6.


