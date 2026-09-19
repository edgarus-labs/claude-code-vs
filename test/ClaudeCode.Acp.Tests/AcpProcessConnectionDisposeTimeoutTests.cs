using ClaudeCode.Acp;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// SetSessionConfigOptionAsync's error-recovery path used to dispose via the same graceful-shutdown
/// timeout (3s) as a normal, healthy shutdown, even though the connection is already known to be
/// broken at that point - needlessly slow. DisposeAsync gained an internal overload that accepts the
/// graceful-shutdown timeout explicitly so the error path can use a shorter one.
/// </summary>
public sealed class AcpProcessConnectionDisposeTimeoutTests
{
    [Fact]
    public async Task DisposeAsync_WithAnExplicitGracefulShutdownTimeout_BoundsHowLongItWaitsBeforeKilling()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // ping ignores stdin closing entirely and keeps running regardless, so the graceful-shutdown
        // wait is bounded by the 200ms timeout before DisposeAsync moves on to killing it. The
        // process spawn, tree kill and stderr drain add their own ~1-2s of overhead that is
        // independent of that wait, so the assertion cannot be a tight absolute bound. It only has
        // to prove the SHORT timeout was used: a regression back to the default 3s would add the
        // full 3s on top of the same overhead, so finishing inside the default window proves it.
        var connection = new AcpProcessConnection("cmd.exe", new[] { "/c", "ping", "-n", "60", "127.0.0.1" });

        var stopwatch = Stopwatch.StartNew();
        await connection.DisposeAsync(TimeSpan.FromMilliseconds(200));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"expected disposal to honor the short explicit graceful-shutdown timeout instead of the default 3s one, took {stopwatch.Elapsed}.");
    }
}
