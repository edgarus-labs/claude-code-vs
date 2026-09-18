using System;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class JsonRpcConnectionTests : IAsyncLifetime, IAsyncDisposable
{
    // "toTest" = what the connection under test WRITES (its outbound requests/notifications/responses);
    // the test reads from toTest.Reader to observe them.
    // "fromTest" = what the connection under test READS (its inbound stream); the test writes fake
    // peer messages into fromTest.Writer.
    private readonly Pipe _toTest = new Pipe();

    private readonly Pipe _fromTest = new Pipe();
    private JsonRpcConnection _connection = null!;

    public Task InitializeAsync()
    {
        _connection = new JsonRpcConnection(_fromTest.Reader.AsStream(), _toTest.Writer.AsStream());
        _connection.Start();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // Explicit IAsyncDisposable purely so CA1001 recognizes this type as disposable; xUnit drives
    // teardown through IAsyncLifetime.DisposeAsync() (Task-returning) above, not through this.
    ValueTask IAsyncDisposable.DisposeAsync() => new ValueTask(DisposeAsync());

    [Fact]
    public async Task SendRequestAsync_CorrelatesResponses_DespiteOutOfOrderRepliesAndAnInterleavedNotification()
    {
        Task<JsonNode?> firstRequest = _connection.SendRequestAsync("methodA", new JsonObject { ["tag"] = "first" }, CancellationToken.None);
        Task<JsonNode?> secondRequest = _connection.SendRequestAsync("methodB", new JsonObject { ["tag"] = "second" }, CancellationToken.None);

        long firstId = await ReadRequestIdAsync("first");
        long secondId = await ReadRequestIdAsync("second");

        // An unrelated notification arrives on the wire before either response - must not disturb correlation.
        await PipeTestHelpers.WriteLineAsync(_fromTest.Writer, "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{}}");

        // Respond out of order relative to the requests being sent (second's id answered before first's).
        await PipeTestHelpers.WriteLineAsync(_fromTest.Writer, $"{{\"jsonrpc\":\"2.0\",\"id\":{secondId},\"result\":{{\"which\":\"second\"}}}}");
        await PipeTestHelpers.WriteLineAsync(_fromTest.Writer, $"{{\"jsonrpc\":\"2.0\",\"id\":{firstId},\"result\":{{\"which\":\"first\"}}}}");

        JsonNode? firstResult = await firstRequest.WaitAsync(TimeSpan.FromSeconds(5));
        JsonNode? secondResult = await secondRequest.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("first", firstResult!["which"]!.GetValue<string>());
        Assert.Equal("second", secondResult!["which"]!.GetValue<string>());
    }

    [Fact]
    public async Task SendRequestAsync_RemoteErrorResponse_FaultsTheCallerWithCodeAndMessage()
    {
        Task<JsonNode?> request = _connection.SendRequestAsync("session/new", new JsonObject(), CancellationToken.None);
        long id = await ReadRequestIdAsync();

        await PipeTestHelpers.WriteLineAsync(_fromTest.Writer, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32000,\"message\":\"boom\"}}}}");

        AcpRemoteException ex = await Assert.ThrowsAsync<AcpRemoteException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(-32000, ex.Code);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task RemoteError_ExposesSafeDetailsAndKind_WithoutRawPayloadOrCredentials()
    {
        Task<JsonNode?> request = _connection.SendRequestAsync("session/new", new JsonObject(), CancellationToken.None);
        long id = await ReadRequestIdAsync();
        var data = new JsonObject
        {
            ["errorKind"] = "authentication_failed",
            ["details"] = "Refresh failed; accessToken=\"fake-access-value\"; https://example.test/?token=fake-query-value",
            ["stderr"] = "private-stderr",
            ["request"] = new JsonObject { ["prompt"] = "private-prompt", ["apiKey"] = "private-key" },
        };
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "Internal error", ["data"] = data },
        };
        await PipeTestHelpers.WriteLineAsync(_fromTest.Writer, envelope.ToJsonString());

        AcpRemoteException error = await Assert.ThrowsAsync<AcpRemoteException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(-32603, error.Code);
        Assert.Contains("authentication_failed", error.Message);
        Assert.Contains("Refresh failed", error.Message);
        Assert.DoesNotContain("fake-access-value", error.Message);
        Assert.DoesNotContain("fake-query-value", error.Message);
        Assert.DoesNotContain("private-stderr", error.Message);
        Assert.DoesNotContain("private-prompt", error.Message);
        Assert.DoesNotContain("private-key", error.Message);
        Assert.Equal("authentication_failed", error.RemoteData!["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public void RemoteError_BoundsFreeText_AndOmitsStructuredOrMultilineData()
    {
        var error = new AcpRemoteException(-32603, string.Concat(System.Linq.Enumerable.Repeat("Failure ", 100)),
            new JsonObject
            {
                ["errorKind"] = "untrusted-secret-kind",
                ["details"] = "Connection closed\nAuthorization: private-header",
            });
        Assert.True(error.Message.Length <= 800);
        Assert.Contains("Connection closed", error.Message);
        Assert.DoesNotContain("private-header", error.Message);
        Assert.DoesNotContain("untrusted-secret-kind", error.Message);

        error = new AcpRemoteException(-32603, "Failed with {\"prompt\":\"private-message\"}",
            new JsonObject { ["details"] = new JsonObject { ["accessToken"] = "private-token" } });
        Assert.DoesNotContain("private-message", error.Message);
        Assert.DoesNotContain("private-token", error.Message);
    }

    [Theory]
    [InlineData("Authorization: Bearer fake-header-value", "fake-header-value")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN=fake-env-value", "fake-env-value")]
    [InlineData("api_key='fake-key-value'", "fake-key-value")]
    [InlineData("Failed using sk-ant-fake-token", "sk-ant-fake-token")]
    public void RemoteError_RedactsCredentialsInTheRemoteMessage(string message, string secret)
    {
        var error = new AcpRemoteException(-32603, message);
        Assert.DoesNotContain(secret, error.Message);
    }

    [Fact]
    public async Task Disconnected_WhenPeerClosesStream_FailsStillPendingRequests()
    {
        Task<JsonNode?> request = _connection.SendRequestAsync("session/new", new JsonObject(), CancellationToken.None);
        await ReadRequestIdAsync(); // drain the write so the request is actually in flight.

        _fromTest.Writer.Complete(); // simulate the child process exiting / closing its stdout.

        await Assert.ThrowsAnyAsync<Exception>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PumpAsync_LineExceedsMaximumLength_DisconnectsInsteadOfBufferingUnbounded()
    {
        // A misbehaving or malicious peer that never sends a newline must not be able to make the
        // client buffer an unbounded amount of memory; the pump must give up past a hard cap.
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.Disconnected += (_, ex) => disconnected.TrySetResult(ex);
        Task<JsonNode?> request = _connection.SendRequestAsync("session/new", new JsonObject(), CancellationToken.None);
        await ReadRequestIdAsync();

        byte[] oversized = new byte[(32 * 1024 * 1024) + 1024];
        for (int i = 0; i < oversized.Length; i++)
        {
            oversized[i] = (byte)'x'; // no newline anywhere in the payload.
        }

        await _fromTest.Writer.WriteAsync(oversized);

        Exception? failure = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(failure);
        await Assert.ThrowsAnyAsync<Exception>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DispatchLine_ManyConcurrentInboundRequests_ThrottlesConcurrentHandlerExecution()
    {
        // A flood of inbound requests from the peer must not fan out into unbounded concurrent
        // handler executions (and the thread-pool pressure that comes with it) - concurrency must
        // be capped even though every request is still eventually served.
        const int totalRequests = 24;
        const int expectedMaxConcurrency = 16;
        int concurrent = 0;
        int maxObservedConcurrent = 0;
        int enteredCount = 0;
        var reachedCap = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _connection.RequestHandler = async (method, @params, ct) =>
        {
            int current = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxObservedConcurrent, current);
            if (Interlocked.Increment(ref enteredCount) == expectedMaxConcurrency)
            {
                reachedCap.TrySetResult(true);
            }

            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
            return new JsonObject();
        };

        for (int i = 0; i < totalRequests; i++)
        {
            await PipeTestHelpers.WriteLineAsync(_fromTest.Writer, $"{{\"jsonrpc\":\"2.0\",\"id\":{i},\"method\":\"custom/test\",\"params\":{{}}}}");
        }

        await reachedCap.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(300)); // let any wrongly-unthrottled handlers start too.
        Assert.Equal(expectedMaxConcurrency, Volatile.Read(ref maxObservedConcurrent));

        release.TrySetResult(true);
        for (int i = 0; i < totalRequests; i++)
        {
            await PipeTestHelpers.ReadLineAsync(_toTest.Reader).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task HandleInboundRequestAsync_LocalHandlerThrows_RespondsWithGenericMessage_NotTheLocalExceptionText()
    {
        // A non-AcpRemoteException thrown by our own (local) request handler must never have its raw
        // .Message echoed back to the remote agent process - it can contain local file paths or other
        // details the agent has no business seeing. Only AcpRemoteException (already redacted) is safe.
        const string sensitivePath = "C:\\Users\\alice\\.ssh\\id_rsa";
        _connection.RequestHandler = (method, @params, ct) => throw new InvalidOperationException("Failed reading " + sensitivePath);

        await PipeTestHelpers.WriteLineAsync(_fromTest.Writer, "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"custom/test\",\"params\":{}}");

        string line = await PipeTestHelpers.ReadLineAsync(_toTest.Reader).WaitAsync(TimeSpan.FromSeconds(5));
        JsonObject response = JsonNode.Parse(line)!.AsObject();

        Assert.Equal(42, response["id"]!.GetValue<int>());
        string message = response["error"]!["message"]!.GetValue<string>();
        Assert.DoesNotContain(sensitivePath, message);
        Assert.DoesNotContain("alice", message);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int initial;
        do
        {
            initial = Volatile.Read(ref target);
            if (value <= initial)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, initial) != initial);
    }

    private async Task<long> ReadRequestIdAsync(string? expectedTag = null)
    {
        string line = await PipeTestHelpers.ReadLineAsync(_toTest.Reader).WaitAsync(TimeSpan.FromSeconds(5));
        JsonObject obj = JsonNode.Parse(line)!.AsObject();
        if (expectedTag is not null)
        {
            Assert.Equal(expectedTag, obj["params"]!["tag"]!.GetValue<string>());
        }

        return obj["id"]!.GetValue<long>();
    }
}
