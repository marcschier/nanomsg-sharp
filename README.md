# NanoMsgSharp

[![CI](https://github.com/marcschier/nanomsg-sharp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/marcschier/nanomsg-sharp/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/NanoMsgSharp?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NanoMsgSharp) [![NuGet (Dtls)](https://img.shields.io/nuget/v/NanoMsgSharp.Dtls?logo=nuget&label=NuGet%20%28Dtls%29)](https://www.nuget.org/packages/NanoMsgSharp.Dtls) [![GitHub Packages](https://img.shields.io/badge/GitHub%20Packages-NanoMsgSharp-2088FF?logo=github&logoColor=white)](https://github.com/marcschier/nanomsg-sharp/pkgs/nuget/NanoMsgSharp)

A pure, dependency-free, **NativeAOT-ready** .NET implementation of the **Scalability Protocols** (SP) — the messaging patterns and on-the-wire protocol shared by [nanomsg](https://github.com/nanomsg/nanomsg) and its successor [NNG](https://nng.nanomsg.org/) (nanomsg-next-gen) — rebuilt from scratch for modern .NET.

`NanoMsgSharp` (assembly/namespace `NanoMsg`) implements the full set of scalability protocols over multiple transports, with an idiomatic asynchronous API and a zero-copy data path built on `System.IO.Pipelines`. It speaks the real SP wire protocol, so a .NET socket interoperates directly with both a C `libnanomsg` peer and a C `libnng` peer.

## ✨ Why NanoMsgSharp

- **Wire-compatible** with both C nanomsg and NNG — validated by interop tests that run a real `libnanomsg` *and* a real `libnng` peer against this library.
- **Zero-copy where it counts**: `PipeReader`/`PipeWriter` all the way down, `ReadOnlySequence<byte>` message slices, `BinaryPrimitives` headers, `ref struct` readers — no allocations on the hot path.
- **Idiomatic modern async**: strongly-typed sockets, `ValueTask` send/receive, `CancellationToken`, `IAsyncDisposable`.
- **NativeAOT & trimming clean** on .NET 8/9/10 — the library is annotated `IsAotCompatible` and the test suite itself is verified running as a NativeAOT binary.
- **No native dependency**: every transport is built on in-box BCL sockets, pipes, TLS, and WebSockets. (The optional `quic://` transport uses the in-box `System.Net.Quic`, which relies on MsQuic — bundled with Windows 11 / Windows Server 2022, or the `libmsquic` package on Linux.)

## Supported target frameworks

| TFM | Notes |
| --- | --- |
| `net10.0`, `net9.0`, `net8.0` | Full feature set; NativeAOT supported. These are the only frameworks the tests, benchmarks, and interop suites run on, and the hot paths are tuned for them. |
| `netstandard2.1` | Broad reach (.NET Core 3.0+, Mono 6.4+, Xamarin, Unity 2021.2+) via polyfills. Full feature set **except** the `ws`/`wss` **server** (`bind`), which throws `PlatformNotSupportedException` — the WebSocket *client* (`connect`) works. |
| `netstandard2.0` | Widest reach (.NET Framework 4.6.2+, older Unity/Mono/Xamarin) via polyfills. The `ws`/`wss` transport is unavailable, and `ipc://` is limited to Windows named pipes (Unix-domain sockets need ns2.1+); all other transports and every protocol work. |

> The `netstandard` builds add polyfill packages (`System.Memory`, `System.IO.Pipelines`, `System.Threading.Channels`, `Microsoft.Bcl.AsyncInterfaces`, PolySharp, …) **only** for those target frameworks. The `net8.0`/`net9.0`/`net10.0` output is byte-identical to a build without netstandard support — there is no performance impact on modern runtimes.
>
> `NanoMsgSharp.Dtls` targets `netstandard2.0`, `netstandard2.1`, `net8.0`, `net9.0`, and `net10.0` (it requires DtlsSharp ≥ 1.0.1, the first DtlsSharp release with a `netstandard2.0` build).

## Scalability protocols

| Pattern | Sockets |
| --- | --- |
| **PAIR** | one-to-one, bidirectional (`PairSocket`; v0 nanomsg-compatible) |
| **PAIR1** | NNG versioned pair with a hop-count (TTL) header and optional polyamorous mode (`Pair1Socket`) |
| **REQ/REP** | request / reply |
| **PUB/SUB** | publish / subscribe (topic-prefix filtering) |
| **PUSH/PULL** | pipeline (load-balanced fan-out / fair-queued fan-in) |
| **SURVEY** | surveyor / respondent |
| **BUS** | many-to-many broadcast |

## 🔌 Transports

| Scheme | Mapping | Package |
| --- | --- | --- |
| `inproc://` | in-process (zero serialization) | core |
| `tcp://` | TCP | core |
| `tls+tcp://` | TLS over TCP (`SslStream`) | core |
| `ipc://` | Unix domain socket (Unix) / named pipe (Windows) | core |
| `ws://` | WebSocket (SP-over-WebSocket mapping) | core |
| `wss://` | WebSocket over TLS | core |
| `udp://` | UDP datagram (one SP message per packet; *experimental*) | core |
| `quic://` | QUIC — SP over a bidirectional QUIC stream, always TLS (**.NET 8+**, needs MsQuic) | core |
| `dtls+udp://` | DTLS-secured UDP datagram | `NanoMsgSharp.Dtls` |

Each scheme also accepts an explicit address-family suffix — `tcp4`/`tcp6`, `tls+tcp4`/`6`, `ws4`/`6`, `wss4`/`6`, `udp4`/`6`, `quic4`/`6`, `dtls+udp4`/`6` — to force IPv4 or IPv6.

TLS, QUIC, and DTLS endpoints are configured through `NanoSocketOptions` (server certificate, client certificates, remote-certificate validation callback, and target host).

The `quic://` transport carries the SP protocol over a single bidirectional QUIC stream using the in-box `System.Net.Quic`. It requires **.NET 8 or later** and a working MsQuic provider (bundled with Windows 11 / Windows Server 2022, or the `libmsquic` package on Linux); on `netstandard` targets, or where MsQuic is unavailable, binding or dialing a `quic://` endpoint throws `PlatformNotSupportedException`.

The `udp://` transport mirrors NNG's *experimental* UDP transport (unreliable, unordered, one SP message per UDP packet, ≤ 65000 bytes). The DTLS transport lives in the optional **`NanoMsgSharp.Dtls`** package (it depends on [DtlsSharp](https://www.nuget.org/packages/DtlsSharp)); reference it and the `dtls+udp://` scheme registers automatically:

```shell
dotnet add package NanoMsgSharp.Dtls
```

## Transport coverage vs the NNG reference

NanoMsgSharp covers **all 10** NNG reference protocols and **6 of the 7** reference transports (inproc, ipc, tcp, tls, WebSocket, udp). The remaining NNG reference transport, the *experimental* `socket://` (BSD-socket / file-descriptor passing), is a deliberate non-goal: it is POSIX-only, listener-only, and exchanges a pre-connected socket rather than a URL, so it does not fit the address-based API. `udp://` and `dtls+udp://` are validated NanoMsgSharp↔NanoMsgSharp (the experimental NNG udp transport is absent from released `libnng`, so it cannot be wire-verified against native NNG).

## 📦 Install

```shell
dotnet add package NanoMsgSharp
```

## 🚀 Quick start

```csharp
using NanoMsg;

// Publisher
await using var pub = new PublishSocket();
await pub.BindAsync("tcp://*:5555");

// Subscriber
await using var sub = new SubscribeSocket();
sub.Connect("tcp://127.0.0.1:5555");
sub.Subscribe("weather");

await pub.SendAsync("weather: sunny"u8.ToArray());
using NanoMessage msg = await sub.ReceiveAsync();
// msg.Payload is a ReadOnlyMemory<byte> backed by a pooled buffer; dispose the message when done.
```

## 📚 Documentation

- [Architecture](docs/architecture.md) — layering, connection lifecycle, and the zero-copy data path.
- [Wire format](docs/wire-format.md) — SP header, framing, per-protocol headers, and the SP-over-WebSocket mapping (interop-verified against C nanomsg and NNG).
- [Benchmarks](docs/benchmarks.md) — how to run the BenchmarkDotNet suite and the native comparison.
- [Interop](interop/README.md) — cross-compatibility coverage against `libnanomsg` and `libnng`.
- [Samples](samples/NanoMsg.Samples) — a runnable tour of every protocol over the in-process and UDP transports.
- [License](LICENSE) · [Third-party notices](NOTICE)

Run the samples with:

```shell
dotnet run --project samples/NanoMsg.Samples
```

### References

- [nanomsg](https://github.com/nanomsg/nanomsg) and [NNG](https://nng.nanomsg.org/) — the reference C implementations NanoMsgSharp interoperates with.
- [DtlsSharp](https://www.nuget.org/packages/DtlsSharp) — the managed DTLS library powering the `dtls+udp://` transport.
- [SP-over-WebSocket mapping](https://github.com/nanomsg/nanomsg/blob/master/rfc/sp-websocket-mapping-01.txt) — the WebSocket framing the `ws`/`wss` transports implement.

## Building & testing

```shell
dotnet build NanoMsg.slnx -c Release
dotnet test NanoMsg.slnx -c Release
```

Interop tests against the native libraries (Linux):

```shell
sudo apt-get install -y libnanomsg-dev libnng-dev
dotnet test tests/NanoMsg.Interop.Tests/NanoMsg.Interop.Tests.csproj -c Release
```

Benchmarks:

```shell
./eng/run-benchmarks.ps1 --filter '*'
```

## Trademarks & attribution

This is an independent, clean-room reimplementation of the **Scalability Protocols (SP)** wire protocol in managed .NET. It is **not** affiliated with, endorsed by, or derived from the source code of the nanomsg or NNG projects.

- **nanomsg** is an open-source project (MIT License) created by Martin Sustrik and contributors. "nanomsg" and related marks belong to their respective owners. See <https://github.com/nanomsg/nanomsg>.
- **NNG** (nanomsg-next-gen) is an open-source project (MIT License) maintained by Staysail Systems, Inc., Garrett D'Amore, and contributors. "NNG" and "nanomsg" and related marks belong to their respective owners. See <https://github.com/nanomsg/nng> and <https://nng.nanomsg.org/>.

NanoMsgSharp interoperates with these libraries by implementing the same publicly documented SP wire protocol and the [SP-over-WebSocket mapping](https://github.com/nanomsg/nanomsg/blob/master/rfc/sp-websocket-mapping-01.txt). The names "nanomsg" and "NNG" are used here only nominatively, to describe that compatibility. The "NanoMsgSharp" / "nanomsg-sharp" naming is chosen to avoid confusion with the original projects, and the published package id is `NanoMsgSharp`.

See [NOTICE](NOTICE) for the third-party attributions.

## 📄 License

[MIT](LICENSE)
