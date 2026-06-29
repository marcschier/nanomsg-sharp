# Samples

A single console project, [`NanoMsg.Samples`](NanoMsg.Samples), that takes a short guided tour through the NanoMsgSharp scalability protocols. Running it executes each demo in turn and prints what every socket sends and receives.

## Run

```shell
dotnet run --project samples/NanoMsg.Samples
```

## What it demonstrates

| Demo | Protocol | Shows |
| --- | --- | --- |
| PUB/SUB | `PublishSocket` / `SubscribeSocket` | Topic-prefix subscription (`weather`) — the matching message is received, the non-matching one is filtered out. Note the slow-joiner pause before publishing. |
| REQ/REP | `RequestSocket` / `ReplySocket` | One request/reply round trip; a background responder echoes the request. |
| PUSH/PULL | `PushSocket` / `PullSocket` | A three-task pipeline fanned out to one puller. |
| PAIR1 | `Pair1Socket` | NNG's versioned pair (hop-count/TTL header) exchanging a message each way; wire-compatible with a C `libnng` pair1 peer. |
| UDP | `Push`/`Pull` over `udp://` | The datagram transport: one SP message per UDP packet. A DTLS-secured variant (`dtls+udp://`) ships in the `NanoMsgSharp.Dtls` package. |

Every demo uses the in-process transport (`inproc://`) except UDP, which uses loopback — so no network setup is needed. The same sockets work unchanged over `tcp`, `tls+tcp`, `ipc`, `ws`/`wss`, and `quic`; just pass a different address (see the transports table in the [root README](../README.md)).

## See also

- [Architecture](../docs/architecture.md) — how protocols and transports are layered.
- [Wire format](../docs/wire-format.md) — the on-the-wire SP handshake and framing.
- [Benchmarks](../docs/benchmarks.md) — protocol × transport × size throughput.
