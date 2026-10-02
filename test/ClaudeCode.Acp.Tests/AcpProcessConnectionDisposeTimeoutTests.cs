using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// Covers the <c>DisposeAsync</c> overload that bounds the graceful-shutdown wait with an explicit
/// timeout before killing the process.
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

        var connection = new AcpProcessConnection("cmd.exe", new[] { "/c", "ping", "-n", "60", "127.0.0.1" });

        var stopwatch = Stopwatch.StartNew();
        await connection.DisposeAsync(TimeSpan.FromMilliseconds(200));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"expected disposal to honor the short explicit graceful-shutdown timeout instead of the default 3s one, took {stopwatch.Elapsed}.");
    }
}
