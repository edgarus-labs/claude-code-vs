using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class WindowsJobProcessInheritanceTests
{
    [Fact]
    public async Task ConcurrentOrdinaryProcessLaunch_DoesNotInheritAdapterPipesOrDelayEof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? ordinary = null;
        Task<int>? eof = null;
        try
        {
            using WindowsJobProcess adapter = WindowsJobProcess.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/c exit 0",
            }, beforeProcessCreate: () =>
            {
                // Start an ordinary child while the adapter's child pipe handles exist. Without
                // isolated inheritance, this child retains the writer and prevents adapter EOF.
                ordinary = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
                    Arguments = "-n 60 127.0.0.1",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
            });
            await adapter.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(ordinary);
            Assert.False(ordinary.HasExited);

            eof = adapter.StandardOutput.ReadAsync(new byte[1], 0, 1);
            Assert.Same(eof, await Task.WhenAny(eof, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.Equal(0, await eof);
            Assert.False(ordinary.HasExited);
        }
        finally
        {
            if (ordinary is not null)
            {
                using (ordinary)
                {
                    if (!ordinary.HasExited)
                    {
                        ordinary.Kill(entireProcessTree: true);
                    }

                    await ordinary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            if (eof is not null)
            {
                try
                {
                    await eof.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (ObjectDisposedException)
                {
                    // On a failed assertion, adapter disposal can close the pending read first.
                }
            }
        }
    }
}
