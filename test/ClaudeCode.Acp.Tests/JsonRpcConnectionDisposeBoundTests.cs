using System;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// DisposeAsync used to await the pump task with no time bound. If a Disconnected subscriber blocks
/// synchronously (the pump's finally block invokes it inline), DisposeAsync could hang forever
/// instead of eventually giving up and completing disposal anyway.
/// </summary>
public sealed class JsonRpcConnectionDisposeBoundTests
{
    [Fact]
    public async Task DisposeAsync_DisconnectedSubscriberBlocksSynchronously_DoesNotHangForever()
    {
        var toTest = new Pipe();
        var fromTest = new Pipe();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), toTest.Writer.AsStream());
        connection.Start();

        var release = new ManualResetEventSlim(false);
        connection.Disconnected += (_, _) => release.Wait(); // simulates a badly-behaved subscriber.

        fromTest.Writer.Complete(); // clean EOF -> the pump's finally block invokes Disconnected inline.

        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set(); // let the blocked pump thread go so it doesn't leak past this test.
        }
    }

    [Fact]
    public async Task DisposeAsync_DisposesCancellationTokenSource()
    {
        var toTest = new Pipe();
        var fromTest = new Pipe();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), toTest.Writer.AsStream());
        connection.Start();

        FieldInfo ctsField = typeof(JsonRpcConnection).GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cts = (CancellationTokenSource)ctsField.GetValue(connection)!;

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // Cancel() throws ObjectDisposedException only once the source itself has been disposed -
        // calling it again on a merely-cancelled-but-undisposed source is a documented no-op, so this
        // is proof _cts.Dispose() actually ran, not just _cts.Cancel().
        Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
    }

    [Fact]
    public async Task DisposeAsync_BlockedInboundRequestHandler_ThrottleReleaseAfterDisposal_NeverEscapesUnobserved()
    {
        var toTest = new Pipe();
        var fromTest = new Pipe();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), toTest.Writer.AsStream());

        var handlerEntered = new SemaphoreSlim(0);
        var releaseHandler = new ManualResetEventSlim(false);
        connection.RequestHandler = (method, @params, ct) =>
        {
            handlerEntered.Release();
            releaseHandler.Wait(TimeSpan.FromSeconds(10), CancellationToken.None); // deliberately not "ct": must survive _cts.Cancel() to simulate a handler still in flight across disposal.
            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        connection.Start();

        byte[] requestBytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"test\",\"params\":{}}\n");
        await fromTest.Writer.WriteAsync(requestBytes);
        await handlerEntered.WaitAsync(TimeSpan.FromSeconds(5));

        // Clean EOF - the pump exits (and thus DisposeAsync's bounded wait on it completes) almost
        // immediately, well before the still-blocked inbound-request handler task returns, because
        // requests are dispatched off the pump via Task.Run rather than awaited inline.
        fromTest.Writer.Complete();

        // TaskScheduler.UnobservedTaskException is process-global and other test classes run
        // concurrently in this assembly, so filter narrowly to the exact defect under test (an
        // ObjectDisposedException raised against _inboundRequestThrottle specifically) rather than
        // any exception, to avoid false positives from unrelated tests' own unobserved tasks.
        ObjectDisposedException? unobserved = null;
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            ObjectDisposedException? match = e.Exception.Flatten().InnerExceptions
                .OfType<ObjectDisposedException>()
                .FirstOrDefault(ode => ode.ObjectName == typeof(SemaphoreSlim).FullName
                    && ode.StackTrace != null && ode.StackTrace.Contains(nameof(JsonRpcConnection), StringComparison.Ordinal));
            if (match is not null)
            {
                unobserved = match;
            }

            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            // Unblock the handler now that _inboundRequestThrottle has already been disposed - its
            // finally block's Release() call is the exact defect under test.
            releaseHandler.Set();

            for (int attempt = 0; attempt < 20 && unobserved is null; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
            releaseHandler.Set();
        }

        Assert.Null(unobserved);
    }
}
