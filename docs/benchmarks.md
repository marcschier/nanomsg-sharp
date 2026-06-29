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

## Native comparison (Linux)

The `NativeThroughputBenchmarks` class P/Invokes the reference C library and therefore only runs where it is installed:

```shell
sudo apt-get install -y libnanomsg-dev
./eng/run-benchmarks.ps1 --filter 'NanoMsg.Benchmarks.NativeThroughputBenchmarks.*'
```

On platforms without `libnanomsg` (for example a typical Windows dev box) this class fails fast in its setup with a clear message — run the managed benchmarks there instead.
