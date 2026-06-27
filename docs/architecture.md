# Architecture

NanoMsgSharp is layered so that each scalability protocol is written once against a transport-agnostic engine, and every transport presents the same `PipeReader`/`PipeWriter` byte channel.

## Layers

```
┌─────────────────────────────────────────────────────────────┐
│ Public sockets (NanoMsg)                                     │
│   PairSocket, PublishSocket/SubscribeSocket,                 │
│   PushSocket/PullSocket, RequestSocket/ReplySocket,          │
│   SurveyorSocket/RespondentSocket, BusSocket, NanoMessage    │
├─────────────────────────────────────────────────────────────┤
│ Protocol engine (NanoMsg.Protocols)                          │
│   NanoSocketCore: endpoint lifecycle, per-pipe read loops,   │
│   fair-queue inbound, broadcast / round-robin send.          │
│   Per-protocol cores add routing + headers.                  │
├─────────────────────────────────────────────────────────────┤
│ Wire (NanoMsg.Wire)                                          │
│   SpHeader (handshake), SpFraming (length prefix +           │
│   4-byte request/survey header), SpProtocol (numbers).       │
├─────────────────────────────────────────────────────────────┤
│ Transports (NanoMsg.Transports)                              │
│   inproc, tcp, tls+tcp, ipc (UDS / named pipe), ws, wss,     │
│   udp, quic (+ dtls+udp via NanoMsgSharp.Dtls) — each an     │
│   INanoConnection exposing PipeReader/PipeWriter.            │
└─────────────────────────────────────────────────────────────┘
```

## Connection lifecycle

- **bind** creates an `INanoListener`; an accept loop performs the SP handshake on each inbound connection and registers a `NanoPipe`.
- **connect** runs a background loop that dials, handshakes, registers a `NanoPipe`, then drives that pipe's read loop until it closes — and reconnects (with `ReconnectInterval` backoff up to `ReconnectIntervalMax`) when it does.
- The SP handshake (`SpHandshake`) writes the local 8-byte `SpHeader`, reads the peer's, and verifies the two protocols are compatible counterparts. The local protocol is threaded to the transport (via `BindAsync`/`ConnectAsync`) so the WebSocket transport can negotiate it through the `Sec-WebSocket-Protocol` sub-protocol.
- The `tls+tcp` and `wss` transports add a TLS (`SslStream`) session under the same framing, configured through `NanoSocketOptions` (certificates, validation callback, target host).
- The `quic` transport (core, .NET 8+) carries the SP protocol over a single bidirectional QUIC stream using the in-box `System.Net.Quic`: the same 8-byte SP handshake and length-prefix framing as the stream transports, always TLS-encrypted (ALPN `nmsg-sp`). It requires MsQuic and throws `PlatformNotSupportedException` where it is unavailable (or on `netstandard`).
- The `ws`/`wss` transports follow the SP-over-WebSocket mapping: the SP handshake is **not** sent on the wire (the protocol is the sub-protocol) and each SP message is one binary WebSocket message. `WebSocketConnection` keeps the rest of the stack unchanged by synthesising the handshake locally and re-framing between length-prefixed frames and whole WebSocket messages.
- The `udp` transport (core) and `dtls+udp` transport (the `NanoMsgSharp.Dtls` package) are datagram transports: each SP message is one datagram. They share an internal `DatagramConnection` re-framer (the same synthesise-handshake + message re-framing approach as WebSockets), over an `IDatagramChannel` backed by a raw UDP socket or a DTLS connection. External transports register with `TransportFactory` for their scheme; `udp` is built in, `dtls+udp` registers from the `NanoMsgSharp.Dtls` assembly.

## Zero-copy data path

The whole stack is built on `System.IO.Pipelines`:

- **Receive:** the per-pipe read loop calls `PipeReader.ReadAsync`, slices complete frames out of the `ReadOnlySequence<byte>` with `SpFraming.TryReadFrame`, hands each frame to the protocol, then `AdvanceTo`. No `byte[]` is allocated on this path until the ownership boundary.
- **Ownership boundary:** a frame slice is valid only until `AdvanceTo`. When a message must outlive the pipe buffer — queued for the application, retained for a REQ resend, or fanned out to a slow PUB/BUS peer — it is copied exactly once into an `ArrayPool`-backed `NanoMessage`.
- **Send:** the length prefix, any 4-byte protocol header, and the body are written straight into `PipeWriter.GetSpan`/`Advance` and flushed — no staging concatenation buffer.

## Routing per protocol

| Protocol | Send | Receive |
| --- | --- | --- |
| PAIR | to the single peer | fair-queue |
| PUB / SUB | broadcast / — | — / prefix-filtered fair-queue |
| PUSH / PULL | round-robin / — | — / fair-queue |
| REQ / REP | round-robin with request id / reply on the source pipe | correlate by id / fair-queue with id |
| SURVEYOR / RESPONDENT | broadcast with survey id / respond on source pipe | collect by id until deadline / fair-queue with id |
| BUS | broadcast | fair-queue |
