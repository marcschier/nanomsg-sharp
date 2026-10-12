# Changelog

## 1.1.3

- Update DtlsSharp to 1.1.1 and the BCL compatibility packages to 10.0.12.
- Update Nerdbank.GitVersioning to 3.10.94, Roslynator analyzers to 5.0.1,
  TUnit to 1.74.0, Rocks to 10.3.4, and code coverage tooling to 18.12.0.
- Use the stable .NET SDK 10.0.401 and update checkout, setup-dotnet, and NuGet login actions.
- Retain the last working stable MsQuic 2.6.0 in Linux CI until stable .NET servicing resolves
  the upstream IP-address handshake regression; do not change TLS validation or skip QUIC tests.
- Build benchmarks in CI and follow the upgraded test runner's coverage report location,
  retaining the 83% line-coverage gate.
- Serialize pipe shutdown with sends so reconnecting cannot complete a writer during frame writes
  or strand queued senders.
- Track accepted connection loops and await all reader owners during shutdown before completing
  their connections, preserving read buffers through `AdvanceTo` and propagating loop errors.
- Propagate WebSocket pump failures as transport I/O errors with the original exception preserved,
  so connection lifecycle handling recognizes remote disconnects.
- Restrict NuGet.org promotion to this repository's NanoMsgSharp and NanoMsgSharp.Dtls packages.
- Retain the stable release channel, assembly identities, and all five supported target frameworks.
