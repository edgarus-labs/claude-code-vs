using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class AcpProcessConnectionStderrTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardError_SensitivePayload_IsNeverPublishedToGlobalTrace(bool subscribe)
    {
        string marker = "acp-secret-" + Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
        await File.WriteAllTextAsync(scriptPath, OperatingSystem.IsWindows()
            ? "@echo off\r\nset /p ready=\r\necho " + marker + " 1>&2\r\n"
            : "read ready\nprintf '%s\\n' '" + marker + "' >&2\n");
        using var listener = new CollectingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            await using var connection = new AcpProcessConnection(
                OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                OperatingSystem.IsWindows() ? new[] { "/c", scriptPath } : new[] { scriptPath });
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (subscribe)
            {
                connection.StandardErrorReceived += (_, line) => received.TrySetResult(line);
            }

            // The adapter emits stderr only after receiving a request, allowing subscription first.
            await Assert.ThrowsAnyAsync<Exception>(() => connection.InitializeAsync(default).WaitAsync(TimeSpan.FromSeconds(10)));
            if (subscribe)
            {
                Assert.Equal(marker, (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).TrimEnd());
            }

            await connection.DisposeAsync();
            Assert.DoesNotContain(listener.Messages, message => message.Contains(marker, StringComparison.Ordinal));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task DisposeAsync_BlockedWindowsStderrSubscriber_ReportsTimeoutWithoutHangingCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new AcpProcessConnection("powershell.exe", new[]
        {
            "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::In.ReadLine() | Out-Null; [Console]::Error.WriteLine('event')",
        });
        connection.StandardErrorReceived += (_, _) =>
        {
            entered.TrySetResult(true);
            release.Wait();
        };
        Task initialize = connection.InitializeAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<Exception>(() => initialize.WaitAsync(TimeSpan.FromSeconds(10)));
            Task disposal = connection.DisposeAsync(TimeSpan.FromMilliseconds(200)).AsTask();
            Assert.Same(disposal, await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(5))));
            await Assert.ThrowsAsync<TimeoutException>(() => disposal);
        }
        finally
        {
            release.Set();
        }
    }

    private sealed class CollectingTraceListener : TraceListener
    {
        public ConcurrentBag<string> Messages { get; } = new ConcurrentBag<string>();

        public override void Write(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        public override void WriteLine(string? message) => Write(message);
    }
}
