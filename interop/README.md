# Native nanomsg and NNG interop

These tests prove **wire-level compatibility** with both the original C
[nanomsg](https://github.com/nanomsg/nanomsg) library and its successor
[NNG](https://nng.nanomsg.org/) (nanomsg-next-gen): a `NanoMsgSharp` socket on one side and a real
`libnanomsg` or `libnng` socket (driven via P/Invoke) on the other must complete each
scalability-protocol exchange over the SP wire protocol. The native libraries are the oracle for
every wire constant.

## How the native libraries are obtained

The shipped `NanoMsgSharp` package has **no** native dependency — `libnanomsg` and `libnng` are
referenced only by this test project, and only at runtime. CI installs them on the Linux runner:

```shell
sudo apt-get update && sudo apt-get install -y libnanomsg-dev libnng-dev
```

Both packages live in Ubuntu's `universe` component (enabled by default on GitHub-hosted runners) and
pull in the runtime shared libraries that `[DllImport("nanomsg")]` and `[DllImport("nng")]` resolve
to (`libnanomsg.so` and `libnng.so`).

## Local runs

The tests probe for each library at startup (`NativeLibrary.TryLoad(...)`) and **skip gracefully**
when it is absent, so the suite is safe to run on Windows/macOS dev machines without the native
libraries installed. To exercise the real cross-compatibility tests locally, install the libraries
(Linux: `apt`; macOS: `brew install nanomsg nng`; or build from source) and run:

```shell
dotnet test tests/NanoMsg.Interop.Tests/NanoMsg.Interop.Tests.csproj -c Release
```

## Coverage

The cross tests pair a NanoMsgSharp socket with a real C socket and exercise every scalability
protocol in both directions, verified end-to-end against `libnanomsg` 1.1.5 and `libnng` 1.5.2:

| Protocol | nanomsg | NNG |
| --- | --- | --- |
| PUSH/PULL | .NET→C and C→.NET (tcp, ipc) | .NET→C and C→.NET (tcp, ipc, ws) |
| PUB/SUB | .NET→C and C→.NET (tcp) | .NET→C and C→.NET (tcp) |
| REQ/REP | .NET→C and C→.NET, tcp (4-byte request id) | .NET→C and C→.NET, tcp (4-byte request id) |
| PAIR (v0) | .NET↔C (tcp) | .NET↔C (tcp) |
| PAIR1 | — (nanomsg has no pair1) | .NET↔C (tcp, TTL header) |
| BUS | .NET→C (tcp) | .NET→C (tcp) |
| SURVEY | .NET→C, tcp (4-byte survey id) | .NET→C, tcp (4-byte survey id) |

The `tcp` and `ipc` (Unix-domain) transports are byte-stream-identical to the reference
implementations. The `ws` transport is **wire-verified against NNG's WebSocket framing** (the
SP-over-WebSocket mapping: sub-protocol negotiation plus one binary message per SP message), in both
directions.

The `tls+tcp` and `wss` transports are validated NanoMsgSharp↔NanoMsgSharp (the Ubuntu `libnng`
package is built without TLS support, so it cannot serve as a TLS oracle); their plaintext framing is
identical to the wire-verified `tcp`/`ws` paths, wrapped in a standard `SslStream` session.

The `udp` and `dtls+udp` (the `NanoMsgSharp.Dtls` package) transports are likewise validated
NanoMsgSharp↔NanoMsgSharp only: NNG's UDP transport is *experimental* and absent from released
`libnng` packages (and its wire format is documented as subject to change), so it cannot be
wire-verified against native NNG.

