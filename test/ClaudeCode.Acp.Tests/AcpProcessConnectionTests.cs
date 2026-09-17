using System;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.Acp.Tests
{
    /// <summary>Exercises <see cref="AcpProcessConnection"/>'s ACP-wire-to-Contracts translation, wired
    /// directly to in-memory <see cref="Pipe"/> pairs via its internal test-only constructor (no real child
    /// process is spawned).</summary>
    public sealed class AcpProcessConnectionTests : IAsyncLifetime
    {
        // "toAgent" = what the connection under test WRITES (requests/notifications/responses it sends
        // toward the "agent"); the test reads from toAgent.Reader to observe them.
        // "fromAgent" = what the connection under test READS; the test writes fake agent messages here.
        private readonly Pipe _toAgent = new Pipe();
        private readonly Pipe _fromAgent = new Pipe();
        private AcpProcessConnection _connection = null!;

        public Task InitializeAsync()
        {
            _connection = new AcpProcessConnection(_fromAgent.Reader.AsStream(), _toAgent.Writer.AsStream());
            return Task.CompletedTask;
        }

        public async Task DisposeAsync() => await _connection.DisposeAsync();

        [Fact]
        public async Task InboundReadTextFileRequest_SurfacesFileReadRequested_AndWritesResponseBackOverTheWire()
        {
            _connection.FileReadRequested += (_, e) =>
            {
                Assert.Equal("/workspace/foo.txt", e.Path);
                Assert.Equal(3, e.Line);
                e.Response.SetResult("hello world");
            };

            await PipeTestHelpers.WriteLineAsync(
                _fromAgent.Writer,
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"fs/read_text_file\",\"params\":{\"sessionId\":\"s1\",\"path\":\"/workspace/foo.txt\",\"line\":3}}");

            string responseLine = await PipeTestHelpers.ReadLineAsync(_toAgent.Reader).WaitAsync(TimeSpan.FromSeconds(5));
            JsonObject response = JsonNode.Parse(responseLine)!.AsObject();

            Assert.Equal(7, response["id"]!.GetValue<int>());
            Assert.Equal("hello world", response["result"]!["content"]!.GetValue<string>());
            Assert.Null(response["error"]);
        }

        [Fact]
        public async Task InboundWriteTextFileRequest_HandlerReportsFailure_RespondsWithJsonRpcError()
        {
            _connection.FileWriteRequested += (_, e) => e.Response.SetResult(false);

            await PipeTestHelpers.WriteLineAsync(
                _fromAgent.Writer,
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"fs/write_text_file\",\"params\":{\"sessionId\":\"s1\",\"path\":\"/readonly.txt\",\"content\":\"x\"}}");

            string responseLine = await PipeTestHelpers.ReadLineAsync(_toAgent.Reader).WaitAsync(TimeSpan.FromSeconds(5));
            JsonObject response = JsonNode.Parse(responseLine)!.AsObject();

            Assert.Equal(9, response["id"]!.GetValue<int>());
            Assert.Null(response["result"]);
            Assert.NotNull(response["error"]);
        }

        [Fact]
        public async Task SessionUpdateNotification_AgentMessageChunk_RaisesSessionUpdateWithAgentMessageChunk()
        {
            var received = new TaskCompletionSource<SessionUpdateEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connection.SessionUpdate += (_, e) => received.TrySetResult(e);

            await PipeTestHelpers.WriteLineAsync(
                _fromAgent.Writer,
                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\"," +
                "\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"Hello there\"}}}}");

            SessionUpdateEventArgs args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("s1", args.SessionId);
            var chunk = Assert.IsType<SessionUpdate.AgentMessageChunk>(args.Update);
            Assert.Equal("Hello there", chunk.Text);
        }

        [Fact]
        public async Task CancelAsync_ResolvesStillPendingPermissionRequest_AsCancelledOutcome()
        {
            // Handler deliberately never completes e.Response - simulating a UI permission dialog the user
            // never answered before the turn was cancelled. Signals once tracked/raised so the test can wait
            // for the request to actually be in flight before racing CancelAsync against it.
            var permissionRequestReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connection.PermissionRequested += (_, _) => permissionRequestReceived.TrySetResult(true);

            await PipeTestHelpers.WriteLineAsync(
                _fromAgent.Writer,
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"s1\"," +
                "\"toolCall\":{\"toolCallId\":\"tc1\",\"title\":\"Run rm\",\"status\":\"pending\"}," +
                "\"options\":[{\"optionId\":\"allow\",\"name\":\"Allow\",\"kind\":\"allow_once\"}]}}");

            await permissionRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await _connection.CancelAsync("s1", CancellationToken.None);

            // A session/cancel notification (no "id") is written first; scan past it to the id:3 response.
            JsonObject response = await ReadResponseWithIdAsync(_toAgent.Reader, 3);
            Assert.Equal("cancelled", response["result"]!["outcome"]!["outcome"]!.GetValue<string>());
        }

        private static async Task<JsonObject> ReadResponseWithIdAsync(PipeReader reader, int expectedId)
        {
            while (true)
            {
                string line = await PipeTestHelpers.ReadLineAsync(reader).WaitAsync(TimeSpan.FromSeconds(5));
                JsonObject obj = JsonNode.Parse(line)!.AsObject();
                if (obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue v && v.TryGetValue<int>(out var id) && id == expectedId)
                {
                    return obj;
                }
            }
        }
    }
}
