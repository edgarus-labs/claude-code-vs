using ClaudeCode.Acp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// AcpProcessConnection only ever surfaced the adapter process's stderr through the
/// StandardErrorReceived event. If nothing subscribes to it (a real possibility - the host UI may
/// wire it up only after construction, or not at all in some paths), diagnostic stderr output
/// (crash stack traces, startup failures) is silently lost with no way to recover it after the fact.
/// A Trace.WriteLine fallback (not Debug.WriteLine, which is compiled out of Release builds) ensures
/// it always reaches at least the trace listeners regardless of subscribers.
/// </summary>
public sealed class AcpProcessConnectionStderrTests
{
    [Fact]
    public async Task Constructor_StandardError_IsAlwaysTraced_EvenWithoutAStandardErrorReceivedSubscriber()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var listener = new CollectingTraceListener();
        Trace.Listeners.Add(listener);
        AcpProcessConnection? connection = null;
        try
        {
            const string marker = "acp-stderr-marker-4f2a";

            // Deliberately never subscribe to StandardErrorReceived - the fallback trace must still fire.
            connection = new AcpProcessConnection("cmd.exe", new[] { "/c", "echo", marker, "1>&2" });

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !listener.Messages.Any(m => m.Contains(marker)))
            {
                await Task.Delay(50);
            }

            Assert.Contains(listener.Messages, m => m.Contains(marker));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private sealed class CollectingTraceListener : TraceListener
    {
        private readonly ConcurrentBag<string> _messages = new ConcurrentBag<string>();

        public ConcurrentBag<string> Messages => _messages;

        public override void Write(string? message)
        {
            if (message is not null)
            {
                _messages.Add(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                _messages.Add(message);
            }
        }
    }
}
