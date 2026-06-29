# Benchmarks

NanoMsgSharp ships a [BenchmarkDotNet](https://benchmarkdotnet.org) suite under `tests/NanoMsg.Benchmarks`. Benchmarks are not run in CI; run them locally on a quiet machine.

## Running

```shell
./eng/run-benchmarks.ps1 --filter '*'
```

`run-benchmarks.ps1` forwards all arguments to BenchmarkDotNet, so you can run the whole matrix, a subset, or a quick non-statistical pass:

```shell
# Whole suite (long — hours; prefer a subset)
./eng/run-benchmarks.ps1 --filter '*'

# One protocol/transport/size cell, fast smoke
./eng/run-benchmarks.ps1 --filter '*MatrixBenchmarks*Tcp*16384*ReqRep*' --job Dry

# A protocol across every transport, or a transport across every protocol
./eng/run-benchmarks.ps1 --filter '*MatrixBenchmarks*PushPull*'
./eng/run-benchmarks.ps1 --filter '*MatrixBenchmarks*Quic*'

# Large messages over stream transports only
./eng/run-benchmarks.ps1 --filter '*LargeMessageBenchmarks*'
```

The matrix reports an **Ops/sec** column (operations per second = `1e9 / Mean(ns)`); one-way protocols count one op per delivered message, REQ/REP and SURVEY count one op per round trip.

## What is measured

| Benchmark | What it covers |
| --- | --- |
| `MatrixBenchmarks` | Every protocol (PUSH/PULL, REQ/REP, PUB/SUB, PAIR, PAIR1, SURVEY, BUS) × every transport (inproc, tcp, ipc, ws, tls+tcp, wss, udp, quic, dtls+udp) × sizes 64/1024/16384 B, reporting ops/sec. quic needs MsQuic; dtls+udp uses `NanoMsgSharp.Dtls`; both fail fast where unavailable. |
| `LargeMessageBenchmarks` | The same protocols over the **stream** transports at 256 KiB / 1 MiB (datagram transports cap a message near 65000 B). |
| `FramingBenchmarks` | The zero-allocation length-prefix framing primitives. Expect **0 B** allocated. |
| `NativeThroughputBenchmarks` | A native `libnanomsg` PUSH/PULL baseline (via P/Invoke) for side-by-side comparison. |

## Example results

Indicative numbers from a Windows dev box (BenchmarkDotNet `ShortRun`, .NET 10) — **not** authoritative. Run the suite at full fidelity on a quiet machine for real comparisons; treat the in-process rows (no OS scheduling or network cost) as the most stable, and the network rows as ballpark (their `ShortRun` variance is high). Ops/sec is messages/sec for one-way protocols and round trips/sec for REQ/REP.

Framing primitives (zero-allocation, very stable):

| Method | Size | Mean | Allocated |
| --- | --- | --- | --- |
| `WriteFrame` | 64 B | ~19 ns | 0 B |
| `WriteFrame` | 64 KiB | ~3.2 µs | 0 B |
| `ReadFrame` | 64 B | ~41 ns | 0 B |
| `ReadFrame` | 64 KiB | ~37 ns | 0 B |

PUSH/PULL throughput, 64-byte messages:

| Transport | Ops/sec | Mean/op |
| --- | --- | --- |
| `inproc` | ~1,300,000 | ~0.75 µs |
| `udp` | ~60,000 | ~17 µs |
| `dtls+udp` | ~48,000 | ~21 µs |
| `quic` | ~35,000 | ~29 µs |
| `tls+tcp` | ~32,000 | ~31 µs |
| `tcp` | ~30,000 | ~30 µs |

REQ/REP round trips, 64-byte messages:

| Transport | Ops/sec |
| --- | --- |
| `inproc` | ~105,000 |
| `tcp` | ~12,000 |
| `tls+tcp` | ~9,000 |

> Datagram transports (`udp`, `dtls+udp`) are unreliable: at large sizes (256 KiB / 1 MiB) loopback drops packets, so the matrix **bounds** the receive wait (≈2 s) rather than hanging — those rows reflect the bound, not sustained throughput. Stream transports carry every byte and report true end-to-end rates.

## Native comparison (Linux)

The `NativeThroughputBenchmarks` class P/Invokes the reference C library and therefore only runs where it is installed:

```shell
sudo apt-get install -y libnanomsg-dev
./eng/run-benchmarks.ps1 --filter 'NanoMsg.Benchmarks.NativeThroughputBenchmarks.*'
```

On platforms without `libnanomsg` (for example a typical Windows dev box) this class fails fast in its setup with a clear message — run the managed benchmarks there instead.
