using System;
using System.IO;
using System.IO.Pipelines;
using System.Collections.Generic;
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
    public async Task DisposeAsync_NotificationSubscriberBlocks_FailsPendingRequestBeforePumpExits()
    {
        var toTest = new Pipe();
        var fromTest = new Pipe();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), toTest.Writer.AsStream());
        using var releaseNotification = new ManualResetEventSlim(false);
        var notificationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.NotificationReceived += (_, _) =>
        {
            notificationEntered.TrySetResult(true);
            releaseNotification.Wait();
        };
        connection.Disconnected += (_, _) => disconnected.TrySetResult(true);
        connection.Start();

        Task<JsonNode?> pending = connection.SendRequestAsync("session/new", new JsonObject(), CancellationToken.None);
        await PipeTestHelpers.ReadLineAsync(toTest.Reader).WaitAsync(TimeSpan.FromSeconds(5));
        await PipeTestHelpers.WriteLineAsync(fromTest.Writer, "{\"jsonrpc\":\"2.0\",\"method\":\"blocked\"}");

        try
        {
            await notificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(disconnected.Task.IsCompleted);
        }
        finally
        {
            releaseNotification.Set();
            await connection.DisposeAsync();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DisposeAsync_ThrowingCancellationCallback_FailsPendingRequestAndPreservesCallbackFailure()
    {
        var toTest = new Pipe();
        var fromTest = new Pipe();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), toTest.Writer.AsStream());
        using var requestCancellation = new CancellationTokenSource();
        var releaseHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailure = new InvalidOperationException("Shutdown callback failed.");
        connection.RequestHandler = async (_, _, cancellationToken) =>
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => throw callbackFailure);
            await releaseHandler.Task;
            return new JsonObject();
        };

        Task<JsonNode?> pending = connection.SendRequestAsync("pending", null, requestCancellation.Token);
        await PipeTestHelpers.ReadLineAsync(toTest.Reader).WaitAsync(TimeSpan.FromSeconds(5));
        Task inbound = connection.HandleInboundRequestThrottledAsync(JsonValue.Create(1L), "held", null);

        try
        {
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
                () => connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(callbackFailure, failure.InnerExceptions);
            await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseHandler.TrySetResult(true);
            requestCancellation.Cancel();
            await inbound.WaitAsync(TimeSpan.FromSeconds(5));
            await Record.ExceptionAsync(() => pending);
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_NoActiveOperations_LateInboundRequestIsCanceled()
    {
        var toTest = new Pipe();
        var fromTest = new Pipe();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), toTest.Writer.AsStream());
        int handlerCalls = 0;
        connection.RequestHandler = (_, _, _) =>
        {
            handlerCalls++;
            return Task.FromResult<JsonNode?>(new JsonObject());
        };

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connection.HandleInboundRequestThrottledAsync(JsonValue.Create(1L), "late", null));
        Assert.Equal(0, handlerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_QueuedInboundRequest_CancelsWithoutInvokingHandler(bool startBeforeDisposal)
    {
        var toTest = new Pipe();
        var fromTest = new Pipe();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), toTest.Writer.AsStream());
        var releaseHandlers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int handlerCalls = 0;
        connection.RequestHandler = async (_, _, _) =>
        {
            Interlocked.Increment(ref handlerCalls);
            await releaseHandlers.Task;
            return new JsonObject();
        };
        var runningHandlers = new List<Task>();
        Task? queued = null;

        try
        {
            // Direct invocation of the dispatch helper deterministically fills every slot before
            // creating the queued operation, without relying on thread-pool scheduling.
            for (int i = 0; i < 16; i++)
            {
                runningHandlers.Add(connection.HandleInboundRequestThrottledAsync(JsonValue.Create((long)i), "held", null));
            }

            Assert.Equal(16, handlerCalls);
            if (startBeforeDisposal)
            {
                queued = connection.HandleInboundRequestThrottledAsync(JsonValue.Create(16L), "queued", null);
                Assert.False(queued.IsCompleted);
            }

            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            // The other ordering models Task.Run work that starts only after teardown finishes.
            queued ??= connection.HandleInboundRequestThrottledAsync(JsonValue.Create(16L), "queued", null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(16, handlerCalls);
        }
        finally
        {
            releaseHandlers.TrySetResult(true);
            await connection.DisposeAsync();
            await Task.WhenAll(runningHandlers).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
