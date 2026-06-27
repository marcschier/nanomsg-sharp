# Benchmarks

NanoMsgSharp ships a [BenchmarkDotNet](https://benchmarkdotnet.org) suite under `tests/NanoMsg.Benchmarks`. Benchmarks are not run in CI; run them locally on a quiet machine.

## Running

```shell
./eng/run-benchmarks.ps1 --filter '*'
```

`run-benchmarks.ps1` forwards all arguments to BenchmarkDotNet, so you can target a subset or pick a job:

```shell
# Just the managed messaging throughput, quick (non-statistical) validation run
./eng/run-benchmarks.ps1 --filter 'NanoMsg.Benchmarks.ThroughputBenchmarks.*' --job Dry

# All managed benchmarks at full fidelity
./eng/run-benchmarks.ps1 --filter 'NanoMsg.Benchmarks.FramingBenchmarks.*' 'NanoMsg.Benchmarks.ThroughputBenchmarks.*' 'NanoMsg.Benchmarks.RequestReplyBenchmarks.*'
```

## What is measured

| Benchmark | What it covers |
| --- | --- |
| `FramingBenchmarks` | The zero-allocation length-prefix framing primitives (write into a reused buffer, read from a sequence over existing memory). Expect **0 B** allocated. |
| `ThroughputBenchmarks` | End-to-end PUSH/PULL message throughput over the in-process transport, isolating the socket/pipe/framing machinery from network cost. The memory diagnoser reports the single pooled copy taken per received message at the ownership boundary. |
| `RequestReplyBenchmarks` | REQ/REP round-trip latency over the in-process transport. |
| `NativeThroughputBenchmarks` | A native `libnanomsg` PUSH/PULL baseline (via P/Invoke) for side-by-side comparison. |

## Native comparison (Linux)

The `NativeThroughputBenchmarks` class P/Invokes the reference C library and therefore only runs where it is installed:

```shell
sudo apt-get install -y libnanomsg-dev
./eng/run-benchmarks.ps1 --filter 'NanoMsg.Benchmarks.NativeThroughputBenchmarks.*'
```

On platforms without `libnanomsg` (for example a typical Windows dev box) this class fails fast in its setup with a clear message — run the managed benchmarks there instead.
