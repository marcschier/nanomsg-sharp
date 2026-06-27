// Copyright (c) marcschier. Licensed under the MIT License.

namespace NanoMsg.Tests;

/// <summary>
/// Minimal toolchain smoke tests that validate the TUnit + Microsoft.Testing.Platform
/// setup (including the NativeAOT publish path) before the real protocol suites land.
/// </summary>
public sealed class SmokeTests
{
    /// <summary>Verifies the test host discovers and runs a trivial test.</summary>
    [Test]
    public async Task Toolchain_Is_Wired_Up()
    {
        int sum = Add(2, 3);
        await Assert.That(sum).IsEqualTo(5);
    }

    /// <summary>Verifies the production assembly is referenced and loadable.</summary>
    [Test]
    public async Task Library_Type_Is_Reachable()
    {
        var ex = new NanoMsgException("boom");
        await Assert.That(ex.Message).IsEqualTo("boom");
    }

    private static int Add(int a, int b) => a + b;
}
