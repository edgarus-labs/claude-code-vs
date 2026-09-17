using System;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests
{
    /// <summary>Exercises <see cref="JsonRpcConnection"/> - the transport/framing layer - directly, against
    /// in-memory <see cref="Pipe"/> pairs standing in for a child process's stdin/stdout.</summary>
    public sealed class JsonRpcConnectionTests : IAsyncLifetime
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
        public async Task Disconnected_WhenPeerClosesStream_FailsStillPendingRequests()
        {
            Task<JsonNode?> request = _connection.SendRequestAsync("session/new", new JsonObject(), CancellationToken.None);
            await ReadRequestIdAsync(); // drain the write so the request is actually in flight.

            _fromTest.Writer.Complete(); // simulate the child process exiting / closing its stdout.

            await Assert.ThrowsAnyAsync<Exception>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        private async Task<long> ReadRequestIdAsync(string? expectedTag = null)
        {
            string line = await PipeTestHelpers.ReadLineAsync(_toTest.Reader).WaitAsync(TimeSpan.FromSeconds(5));
            JsonObject obj = JsonNode.Parse(line)!.AsObject();
            if (expectedTag != null)
            {
                Assert.Equal(expectedTag, obj["params"]!["tag"]!.GetValue<string>());
            }

            return obj["id"]!.GetValue<long>();
        }
    }
}
