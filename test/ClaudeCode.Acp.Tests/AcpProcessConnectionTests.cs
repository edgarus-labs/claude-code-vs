using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class AcpProcessConnectionTests : IAsyncLifetime, IAsyncDisposable
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

    // Explicit IAsyncDisposable purely so CA1001 recognizes this type as disposable; xUnit drives
    // teardown through IAsyncLifetime.DisposeAsync() (Task-returning) above, not through this.
    ValueTask IAsyncDisposable.DisposeAsync() => new ValueTask(DisposeAsync());

    [Fact]
    public async Task InboundReadTextFileRequest_SurfacesFileReadRequested_AndWritesResponseBackOverTheWire()
    {
        _connection.FileReadRequested += (_, e) =>
        {
            Assert.Equal("/workspace/foo.txt", e.Path);
            Assert.Equal(3, e.Line);
            e.Response.TrySetResult("hello world");
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
        _connection.FileWriteRequested += (_, e) => e.Response.TrySetResult(false);

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
    public async Task SessionUpdateNotification_AvailableCommands_PreservesMetadataAndCompleteReplacements()
    {
        var received = new TaskCompletionSource<IReadOnlyList<SessionUpdateEventArgs>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<SessionUpdateEventArgs>();
        _connection.SessionUpdate += (_, update) =>
        {
            notifications.Add(update);
            if (notifications.Count == 3)
            {
                received.TrySetResult(notifications);
            }
        };

        foreach (string commands in new[]
        {
            """
            [
              {"name":"Plugin:review-code","description":"Review changes\nKeep exact casing.","input":{"hint":"<file> [focus area]"}},
              {"name":"mcp:docs:search","description":"Search documentation","input":null},
              {"name":"nested/skill","description":"Run a discovered skill"}
            ]
            """,
            """
            [{"name":"Plugin:review-code","description":"Updated description","input":{"hint":"<new arguments>"}}]
            """,
            "[]",
        })
        {
            await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
                """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"available_commands_update","availableCommands":"""
                + JsonNode.Parse(commands)!.ToJsonString() + "}}}");
        }

        IReadOnlyList<SessionUpdateEventArgs> updates = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(updates, update => Assert.Equal("s1", update.SessionId));
        var initial = Assert.IsType<SessionUpdate.AvailableCommandsChanged>(updates[0].Update);
        Assert.Collection(initial.Commands,
            command =>
            {
                Assert.Equal("Plugin:review-code", command.Name);
                Assert.Equal("Review changes\nKeep exact casing.", command.Description);
                Assert.Equal("<file> [focus area]", command.InputHint);
            },
            command =>
            {
                Assert.Equal("mcp:docs:search", command.Name);
                Assert.Equal("Search documentation", command.Description);
                Assert.Null(command.InputHint);
            },
            command =>
            {
                Assert.Equal("nested/skill", command.Name);
                Assert.Equal("Run a discovered skill", command.Description);
                Assert.Null(command.InputHint);
            });
        var replacement = Assert.IsType<SessionUpdate.AvailableCommandsChanged>(updates[1].Update);
        AvailableCommand changed = Assert.Single(replacement.Commands);
        Assert.Equal("Plugin:review-code", changed.Name);
        Assert.Equal("Updated description", changed.Description);
        Assert.Equal("<new arguments>", changed.InputHint);
        Assert.Empty(Assert.IsType<SessionUpdate.AvailableCommandsChanged>(updates[2].Update).Commands);
    }

    [Fact]
    public async Task SessionUpdate_UsageUpdate_IsSurfacedWithTokensWindowAndCost()
    {
        var received = new TaskCompletionSource<List<SessionUpdateEventArgs>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<SessionUpdateEventArgs>();
        _connection.SessionUpdate += (_, update) =>
        {
            notifications.Add(update);
            if (notifications.Count == 2) received.TrySetResult(notifications);
        };

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"usage_update","used":8300,"size":200000,"cost":{"amount":0.125,"currency":"USD"}}}}""");
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"usage_update","used":9100}}}""");

        var updates = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = Assert.IsType<SessionUpdate.UsageUpdate>(updates[0].Update);
        Assert.Equal(8300, first.UsedTokens);
        Assert.Equal(200000, first.ContextWindowSize);
        Assert.Equal(0.125m, first.CostAmount);
        Assert.Equal("USD", first.CostCurrency);
        var second = Assert.IsType<SessionUpdate.UsageUpdate>(updates[1].Update);
        Assert.Equal(9100, second.UsedTokens);
        Assert.Null(second.ContextWindowSize);
        Assert.Null(second.CostAmount);
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_SendsOnlyOneInitializeRequestOverTheWire()
    {
        var firstCall = _connection.InitializeAsync(CancellationToken.None);

        string requestLine = await PipeTestHelpers.ReadLineAsync(_toAgent.Reader).WaitAsync(TimeSpan.FromSeconds(5));
        JsonObject request = JsonNode.Parse(requestLine)!.AsObject();
        Assert.Equal("initialize", request["method"]!.GetValue<string>());
        int requestId = request["id"]!.GetValue<int>();

        await PipeTestHelpers.WriteLineAsync(
            _fromAgent.Writer,
            $"{{\"jsonrpc\":\"2.0\",\"id\":{requestId},\"result\":{{\"protocolVersion\":1}}}}");

        await firstCall.WaitAsync(TimeSpan.FromSeconds(5));

        // A second call must be a no-op: no further bytes are written toward the agent, so a
        // short read timeout proves nothing else was sent (a real second "initialize" would have
        // arrived immediately since there is no other work pending).
        await _connection.InitializeAsync(CancellationToken.None);

        Task<string> secondReadAttempt = PipeTestHelpers.ReadLineAsync(_toAgent.Reader);
        Task completedFirst = await Task.WhenAny(secondReadAttempt, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(secondReadAttempt, completedFirst);
    }

    [Fact]
    public async Task InitializeAsync_AdvertisesFormElicitationCapability()
    {
        var pending = _connection.InitializeAsync(CancellationToken.None);
        JsonObject request = await ReadRequestAsync("initialize");

        Assert.NotNull(request["params"]!["clientCapabilities"]!["elicitation"]!["form"]);

        // url mode is deliberately NOT advertised: HandleCreateElicitationAsync cannot render it and
        // answers such a request with -32602, so claiming the capability would invite exactly the
        // secret-bearing flows this client cannot honour.
        Assert.Null(request["params"]!["clientCapabilities"]!["elicitation"]!["url"]);

        await ReplyAsync(request, """{"protocolVersion":1}""");
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InboundElicitationCreateRequest_FormMode_ParsesFieldsAndRoundTripsAcceptedAnswer()
    {
        var received = new TaskCompletionSource<ElicitationRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.ElicitationRequested += (_, e) => received.TrySetResult(e);

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, JsonNode.Parse("""
            {"jsonrpc":"2.0","id":21,"method":"elicitation/create","params":{
              "mode":"form","sessionId":"s1","message":"Pick one",
              "requestedSchema":{"type":"object","properties":{
                "question_0":{"type":"string","title":"Color","oneOf":[
                  {"const":"red","title":"Red"},{"const":"blue","title":"Blue","description":"Cool colors"}
                ]},
                "question_0_custom":{"type":"string","title":"Other"},
                "question_1":{"type":"array","title":"Sides","items":{"anyOf":[
                  {"const":"left","title":"Left"},{"const":"right","title":"Right"}
                ]}}
              }}
            }}
            """)!.ToJsonString());

        ElicitationRequestEventArgs args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("s1", args.SessionId);
        Assert.Equal("Pick one", args.Message);
        Assert.Collection(args.Fields,
            field =>
            {
                Assert.Equal("question_0", field.Key);
                Assert.Equal("Color", field.Title);
                Assert.Equal(ElicitationFieldKind.SingleSelect, field.Kind);
                Assert.Collection(field.Options,
                    option => { Assert.Equal("red", option.Value); Assert.Equal("Red", option.Label); },
                    option => { Assert.Equal("blue", option.Value); Assert.Equal("Cool colors", option.Description); });
            },
            field =>
            {
                Assert.Equal("question_0_custom", field.Key);
                Assert.Equal(ElicitationFieldKind.Text, field.Kind);
            },
            field =>
            {
                Assert.Equal("question_1", field.Key);
                Assert.Equal(ElicitationFieldKind.MultiSelect, field.Kind);
                Assert.Collection(field.Options,
                    option => Assert.Equal("left", option.Value),
                    option => Assert.Equal("right", option.Value));
            });

        args.Response.TrySetResult(new ElicitationAnswer(ElicitationAction.Accept, new Dictionary<string, IReadOnlyList<string>>
        {
            ["question_0"] = new[] { "red" },
            ["question_1"] = new[] { "left", "right" },
        }));

        JsonObject response = await ReadResponseWithIdAsync(_toAgent.Reader, 21);
        Assert.Equal("accept", response["result"]!["action"]!.GetValue<string>());
        Assert.Equal("red", response["result"]!["content"]!["question_0"]!.GetValue<string>());
        Assert.Equal(2, response["result"]!["content"]!["question_1"]!.AsArray().Count);
        Assert.Null(response["result"]!["content"]!["question_0_custom"]); // left blank -> omitted, not an empty string.
    }

    [Fact]
    public async Task InboundElicitationCreateRequest_UnsupportedMode_RespondsWithInvalidParams()
    {
        // ACP: "Requests using a mode the Client has not advertised produce JSON-RPC -32602". A
        // `decline` here would be read as "the user refused", which would let a conforming agent
        // retry the same sensitive flow as a form instead of giving up on url mode.
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","id":22,"method":"elicitation/create","params":{"mode":"url","sessionId":"s1","message":"Open this","url":"https://example.test"}}""");

        JsonObject response = await ReadResponseWithIdAsync(_toAgent.Reader, 22);
        Assert.Null(response["result"]);
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task InboundElicitationCreateRequest_RequestScopedForm_RespondsWithInvalidParamsNotInternalError()
    {
        // ACP's request scope (`requestId` instead of `sessionId`, for prompts raised before any
        // session exists) is spec-valid; this client has no surface for it. -32602 names the
        // unsupported scope so a conforming agent can fall back, where reporting a missing required
        // field gives the agent a generic -32603 "Internal error" - "the client is broken".
        _connection.ElicitationRequested += (_, e) =>
            e.Response.TrySetResult(new ElicitationAnswer(ElicitationAction.Accept, new Dictionary<string, IReadOnlyList<string>>()));

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, JsonNode.Parse("""
            {"jsonrpc":"2.0","id":23,"method":"elicitation/create","params":{
              "mode":"form","requestId":"r1","message":"Pick one",
              "requestedSchema":{"type":"object","properties":{}}
            }}
            """)!.ToJsonString());

        JsonObject response = await ReadResponseWithIdAsync(_toAgent.Reader, 23);
        Assert.Null(response["result"]);
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task InboundElicitationCreateRequest_NonStringTypedField_IsOmittedInsteadOfAnsweredWithAJsonString()
    {
        // ACP's ElicitationPropertySchema also covers boolean/number/integer. Those render as free
        // text here, but answering {"type":"boolean"} with the JSON string "true" violates the very
        // schema the agent published, so the field must come back unanswered.
        var received = new TaskCompletionSource<ElicitationRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.ElicitationRequested += (_, e) => received.TrySetResult(e);

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, JsonNode.Parse("""
            {"jsonrpc":"2.0","id":24,"method":"elicitation/create","params":{
              "mode":"form","sessionId":"s1","message":"Confirm",
              "requestedSchema":{"type":"object","properties":{
                "agree":{"type":"boolean","title":"Agree"},
                "note":{"type":"string","title":"Note"}
              }}
            }}
            """)!.ToJsonString());

        ElicitationRequestEventArgs args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        args.Response.TrySetResult(new ElicitationAnswer(ElicitationAction.Accept, new Dictionary<string, IReadOnlyList<string>>
        {
            ["agree"] = new[] { "true" },
            ["note"] = new[] { "looks good" },
        }));

        JsonObject response = await ReadResponseWithIdAsync(_toAgent.Reader, 24);
        Assert.Null(response["result"]!["content"]!["agree"]);
        Assert.Equal("looks good", response["result"]!["content"]!["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task InboundElicitationCreateRequest_UntitledEnumSchemas_ParseAsSelectFieldsNotFreeText()
    {
        // ACP's StringPropertySchema: "When `enum` or `oneOf` is set, this represents a single-select
        // enum"; MultiSelectItems likewise allows `items.enum`. Rendering either as a text box would
        // let the user answer outside the closed set the agent declared.
        var received = new TaskCompletionSource<ElicitationRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.ElicitationRequested += (_, e) => received.TrySetResult(e);

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, JsonNode.Parse("""
            {"jsonrpc":"2.0","id":25,"method":"elicitation/create","params":{
              "mode":"form","sessionId":"s1","message":"Pick one",
              "requestedSchema":{"type":"object","properties":{
                "risk":{"type":"string","title":"Risk","enum":["conservative","balanced","aggressive"]},
                "areas":{"type":"array","title":"Areas","items":{"enum":["api","ui"]}}
              }}
            }}
            """)!.ToJsonString());

        ElicitationRequestEventArgs args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Collection(args.Fields,
            risk =>
            {
                Assert.Equal(ElicitationFieldKind.SingleSelect, risk.Kind);
                Assert.Collection(risk.Options,
                    option => { Assert.Equal("conservative", option.Value); Assert.Equal("conservative", option.Label); },
                    option => Assert.Equal("balanced", option.Value),
                    option => Assert.Equal("aggressive", option.Value));
            },
            areas =>
            {
                Assert.Equal(ElicitationFieldKind.MultiSelect, areas.Kind);
                Assert.Collection(areas.Options,
                    option => Assert.Equal("api", option.Value),
                    option => Assert.Equal("ui", option.Value));
            });
    }

    [Fact]
    public async Task InboundElicitationCreateRequest_OptionWithoutTitle_FallsBackToItsConstAsTheLabel()
    {
        // `title` is an optional JSON Schema annotation - an agent may legitimately emit an option
        // carrying only `const`. Such a prompt must still reach the user, not fail the request.
        ElicitationRequestEventArgs? captured = null;
        _connection.ElicitationRequested += (_, e) =>
        {
            captured = e;
            e.Response.TrySetResult(new ElicitationAnswer(ElicitationAction.Accept, new Dictionary<string, IReadOnlyList<string>>
            {
                ["question_0"] = new[] { "red" },
            }));
        };

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, JsonNode.Parse("""
            {"jsonrpc":"2.0","id":24,"method":"elicitation/create","params":{
              "mode":"form","sessionId":"s1","message":"Pick one",
              "requestedSchema":{"type":"object","properties":{
                "question_0":{"type":"string","oneOf":[{"const":"red"},{"const":"blue","title":"Blue"}]}
              }}
            }}
            """)!.ToJsonString());

        JsonObject response = await ReadResponseWithIdAsync(_toAgent.Reader, 24);
        Assert.True(response["error"] is null, "elicitation/create was rejected instead of surfaced: " + response.ToJsonString());
        Assert.Equal("red", response["result"]!["content"]!["question_0"]!.GetValue<string>());
        ElicitationField field = Assert.Single(captured!.Fields);
        Assert.Collection(field.Options,
            option => { Assert.Equal("red", option.Value); Assert.Equal("red", option.Label); },
            option => { Assert.Equal("blue", option.Value); Assert.Equal("Blue", option.Label); });
    }

    [Fact]
    public async Task CancelAsync_ResolvesStillPendingElicitationRequest_AsCancelledAction()
    {
        var elicitationReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.ElicitationRequested += (_, _) => elicitationReceived.TrySetResult(true);

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, JsonNode.Parse("""
            {"jsonrpc":"2.0","id":23,"method":"elicitation/create","params":{
              "mode":"form","sessionId":"s1","message":"Pick one",
              "requestedSchema":{"type":"object","properties":{
                "question_0":{"type":"string","oneOf":[{"const":"red","title":"Red"}]}
              }}
            }}
            """)!.ToJsonString());

        await elicitationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _connection.CancelAsync("s1", CancellationToken.None);

        JsonObject response = await ReadResponseWithIdAsync(_toAgent.Reader, 23);
        Assert.Equal("cancel", response["result"]!["action"]!.GetValue<string>());
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

    [Fact]
    public async Task CancelAsync_NotificationWriteFails_StillResolvesPendingPermissionAndElicitation()
    {
        var permissionReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.PermissionRequested += (_, _) => permissionReceived.TrySetResult(true);
        var elicitationReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.ElicitationRequested += (_, _) => elicitationReceived.TrySetResult(true);

        await PipeTestHelpers.WriteLineAsync(
            _fromAgent.Writer,
            "{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"s1\"," +
            "\"toolCall\":{\"toolCallId\":\"tc1\",\"title\":\"Run rm\",\"status\":\"pending\"}," +
            "\"options\":[{\"optionId\":\"allow\",\"name\":\"Allow\",\"kind\":\"allow_once\"}]}}");
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, JsonNode.Parse("""
            {"jsonrpc":"2.0","id":32,"method":"elicitation/create","params":{
              "mode":"form","sessionId":"s1","message":"Pick one",
              "requestedSchema":{"type":"object","properties":{
                "question_0":{"type":"string","oneOf":[{"const":"red","title":"Red"}]}
              }}
            }}
            """)!.ToJsonString());
        await permissionReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await elicitationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The session/cancel write itself fails (here: an already-cancelled token, the same shape as
        // an IOException from a dead transport) - precisely when the in-flight prompts most need
        // resolving. The caller still observes the failure...
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _connection.CancelAsync("s1", cts.Token));

        // ...but neither prompt may be left hanging in the UI forever.
        var responses = new Dictionary<int, JsonObject>();
        while (responses.Count < 2)
        {
            string line = await PipeTestHelpers.ReadLineAsync(_toAgent.Reader).WaitAsync(TimeSpan.FromSeconds(5));
            JsonObject message = JsonNode.Parse(line)!.AsObject();
            responses[message["id"]!.GetValue<int>()] = message;
        }

        Assert.Equal("cancelled", responses[31]["result"]!["outcome"]!["outcome"]!.GetValue<string>());
        Assert.Equal("cancel", responses[32]["result"]!["action"]!.GetValue<string>());
    }

    [Fact]
    public async Task RequestPermission_AfterResolution_RemovesEmptySessionBagInsteadOfLeakingItForever()
    {
        _connection.PermissionRequested += (_, e) => e.Response.TrySetResult(e.Options[0].OptionId);

        await PipeTestHelpers.WriteLineAsync(
            _fromAgent.Writer,
            "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"leak-session\"," +
            "\"toolCall\":{\"toolCallId\":\"tc1\",\"title\":\"Run\",\"status\":\"pending\"}," +
            "\"options\":[{\"optionId\":\"allow\",\"name\":\"Allow\",\"kind\":\"allow_once\"}]}}");

        await ReadResponseWithIdAsync(_toAgent.Reader, 11);

        FieldInfo field = typeof(AcpProcessConnection).GetField("_pendingPermissionsBySession", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var bySession = (System.Collections.IDictionary)field.GetValue(_connection)!;
        Assert.False(bySession.Contains("leak-session"),
            "a resolved session's now-empty permission bag must be removed, not retained for the life of the connection.");
    }

    [Fact]
    public async Task DisposeAsync_OnAHealthyConnection_DoesNotRaiseDisconnected()
    {
        // The consumer initiated this shutdown itself by calling DisposeAsync(); it must not also
        // receive an unsolicited "the connection was lost" notification for its own intentional action.
        int disconnectedCount = 0;
        _connection.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);

        await _connection.DisposeAsync();

        Assert.Equal(0, disconnectedCount);
    }

    [Fact]
    public async Task HandleReadTextFileAsync_CancelledWhilePending_UnblocksInsteadOfHangingForever()
    {
        var handlerInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.FileReadRequested += (_, e) => handlerInvoked.TrySetResult(true); // deliberately never resolves e.Response.

        using var cts = new CancellationTokenSource();
        MethodInfo method = typeof(AcpProcessConnection).GetMethod("HandleReadTextFileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var resultTask = (Task<JsonNode?>)method.Invoke(_connection, new object[] { new JsonObject { ["path"] = "/workspace/foo.txt" }, cts.Token })!;

        await handlerInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resultTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task HandleWriteTextFileAsync_CancelledWhilePending_UnblocksInsteadOfHangingForever()
    {
        var handlerInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.FileWriteRequested += (_, e) => handlerInvoked.TrySetResult(true); // deliberately never resolves e.Response.

        using var cts = new CancellationTokenSource();
        MethodInfo method = typeof(AcpProcessConnection).GetMethod("HandleWriteTextFileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var resultTask = (Task<JsonNode?>)method.Invoke(_connection, new object[]
        {
            new JsonObject { ["path"] = "/workspace/foo.txt", ["content"] = "x" }, cts.Token,
        })!;

        await handlerInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resultTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task HandleRequestPermissionAsync_CancelledWhilePending_UnblocksInsteadOfHangingForever()
    {
        var handlerInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.PermissionRequested += (_, e) => handlerInvoked.TrySetResult(true); // deliberately never resolves e.Response.

        using var cts = new CancellationTokenSource();
        MethodInfo method = typeof(AcpProcessConnection).GetMethod("HandleRequestPermissionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var paramsObj = new JsonObject
        {
            ["sessionId"] = "s1",
            ["toolCall"] = new JsonObject { ["toolCallId"] = "tc1", ["title"] = "Run", ["status"] = "pending" },
            ["options"] = new JsonArray(new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" }),
        };
        var resultTask = (Task<JsonNode?>)method.Invoke(_connection, new object[] { paramsObj, cts.Token })!;

        await handlerInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resultTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancelAsync_ConcurrentPermissionCompletionAndRegistration_CancelsEveryOutstandingRequest()
    {
        var outstanding = new System.Collections.Concurrent.ConcurrentBag<Task<JsonNode?>>();
        _connection.PermissionRequested += (_, args) =>
        {
            if (args.Call.ToolCallId == "complete")
            {
                args.Response.TrySetResult("allow");
            }
        };
        MethodInfo method = typeof(AcpProcessConnection).GetMethod("HandleRequestPermissionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var cancellation = new CancellationTokenSource();
        using var start = new ManualResetEventSlim();
        var workers = new Task[8];
        for (int worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Run(() =>
            {
                start.Wait();
                for (int iteration = 0; iteration < 2000; iteration++)
                {
                    // Each pair races the last completion in a session with a new pending request.
                    // Distinct sessions let the race repeat without retaining an always-nonempty bag.
                    string sessionId = "race-" + iteration;
                    foreach (string toolCallId in new[] { "complete", "pending" })
                    {
                        var parameters = new JsonObject
                        {
                            ["sessionId"] = sessionId,
                            ["toolCall"] = new JsonObject { ["toolCallId"] = toolCallId },
                            ["options"] = new JsonArray(),
                        };
                        outstanding.Add((Task<JsonNode?>)method.Invoke(_connection, new object[] { parameters, cancellation.Token })!);
                    }
                }
            });
        }

        try
        {
            start.Set();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
            for (int iteration = 0; iteration < 2000; iteration++)
            {
                await _connection.CancelAsync("race-" + iteration, CancellationToken.None);
                await ReadRequestAsync("session/cancel");
            }

            JsonNode?[] responses = await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (JsonNode? response in responses)
            {
                string outcome = response!["outcome"]!["outcome"]!.GetValue<string>();
                Assert.True(outcome == "selected" || outcome == "cancelled");
            }
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task NewSessionAsync_ReadsCurrentConfigOptions_AndFlattensGroupedChoices()
    {
        Task<NewSessionResult> pending = _connection.NewSessionAsync("/workspace", null, CancellationToken.None);
        JsonObject request = await ReadRequestAsync("session/new");
        Assert.Empty(request["params"]!["mcpServers"]!.AsArray());

        await ReplyAsync(request, """
            {
              "sessionId": "s1",
              "configOptions": [
                {
                  "id": "model", "name": "Model", "category": "model", "type": "select",
                  "currentValue": "outside-picker",
                  "options": [
                    { "group": "recommended", "name": "Recommended", "options": [
                      { "value": "sonnet", "name": "Sonnet", "description": "Balanced" }
                    ] },
                    { "group": "other", "name": "Other", "options": [
                      { "value": "haiku", "name": "Haiku", "description": "Fast" }
                    ] }
                  ]
                },
                {
                  "id": "effort", "name": "Effort", "category": "thought_level", "type": "select",
                  "currentValue": "medium",
                  "options": [
                    { "value": "default", "name": "Default" },
                    { "value": "medium", "name": "Medium" }
                  ]
                }
              ]
            }
            """);

        NewSessionResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("s1", result.SessionId);
        Assert.Collection(result.ConfigOptions,
            model =>
            {
                Assert.Equal("model", model.Id);
                Assert.Equal("model", model.Category);
                Assert.Equal("outside-picker", model.CurrentValue);
                Assert.Collection(model.Options,
                    value =>
                    {
                        Assert.Equal("sonnet", value.Value);
                        Assert.Equal("Sonnet", value.Name);
                        Assert.Equal("Balanced", value.Description);
                    },
                    value => Assert.Equal("haiku", value.Value));
            },
            effort =>
            {
                Assert.Equal("thought_level", effort.Category);
                Assert.Equal("medium", effort.CurrentValue);
                Assert.Equal("default", effort.Options[0].Value);
            });
    }

    [Fact]
    public async Task ListSessionsAsync_SendsCwdFilter_AndParsesSessionSummaries()
    {
        Task<IReadOnlyList<SessionSummary>> pending = _connection.ListSessionsAsync("/workspace", CancellationToken.None);
        JsonObject request = await ReadRequestAsync("session/list");
        Assert.Equal("/workspace", request["params"]!["cwd"]!.GetValue<string>());

        await ReplyAsync(request, """
            {
              "sessions": [
                { "sessionId": "s1", "cwd": "/workspace", "title": "Fix the bug", "updatedAt": "2026-09-18T12:00:00Z" },
                { "sessionId": "s2", "cwd": "/workspace", "updatedAt": null }
              ]
            }
            """);

        IReadOnlyList<SessionSummary> result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Collection(result,
            first =>
            {
                Assert.Equal("s1", first.SessionId);
                Assert.Equal("/workspace", first.Cwd);
                Assert.Equal("Fix the bug", first.Title);
                Assert.Equal(DateTimeOffset.Parse("2026-09-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), first.UpdatedAt);
            },
            second =>
            {
                Assert.Equal("s2", second.SessionId);
                Assert.Null(second.Title);
                Assert.Null(second.UpdatedAt);
            });
    }

    [Fact]
    public async Task ListSessionsAsync_OneUnparseableUpdatedAt_StillReturnsEveryOtherSession()
    {
        Task<IReadOnlyList<SessionSummary>> pending = _connection.ListSessionsAsync(null, CancellationToken.None);
        JsonObject request = await ReadRequestAsync("session/list");

        await ReplyAsync(request, """
            {
              "sessions": [
                { "sessionId": "s1", "cwd": "/workspace", "updatedAt": "yesterday" },
                { "sessionId": "s2", "cwd": "/workspace", "updatedAt": "" },
                { "sessionId": "s3", "cwd": "/workspace", "updatedAt": "2026-09-18T12:00:00Z" }
              ]
            }
            """);

        IReadOnlyList<SessionSummary> result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Collection(result,
            first => { Assert.Equal("s1", first.SessionId); Assert.Null(first.UpdatedAt); },
            second => { Assert.Equal("s2", second.SessionId); Assert.Null(second.UpdatedAt); },
            third =>
            {
                Assert.Equal("s3", third.SessionId);
                Assert.Equal(DateTimeOffset.Parse("2026-09-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), third.UpdatedAt);
            });
    }

    [Fact]
    public async Task LoadSessionAsync_SendsSessionIdCwdAndEmptyMcpServers_AndParsesConfigOptions()
    {
        Task<NewSessionResult> pending = _connection.LoadSessionAsync("s1", "/workspace", null, CancellationToken.None);
        JsonObject request = await ReadRequestAsync("session/load");
        Assert.Equal("s1", request["params"]!["sessionId"]!.GetValue<string>());
        Assert.Equal("/workspace", request["params"]!["cwd"]!.GetValue<string>());
        Assert.Empty(request["params"]!["mcpServers"]!.AsArray());

        await ReplyAsync(request, """
            {
              "configOptions": [
                { "id": "model", "name": "Model", "category": "model", "type": "select", "currentValue": "sonnet",
                  "options": [ { "value": "sonnet", "name": "Sonnet" } ] }
              ]
            }
            """);

        NewSessionResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("s1", result.SessionId);
        Assert.Equal("model", Assert.Single(result.ConfigOptions).Id);
    }

    [Fact]
    public async Task SessionUpdateNotification_UserMessageChunk_RaisesSessionUpdateWithUserMessageChunk()
    {
        var received = new TaskCompletionSource<SessionUpdateEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.SessionUpdate += (_, e) => received.TrySetResult(e);

        await PipeTestHelpers.WriteLineAsync(
            _fromAgent.Writer,
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\"," +
            "\"update\":{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"What does this do?\"}}}}");

        SessionUpdateEventArgs args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("s1", args.SessionId);
        var chunk = Assert.IsType<SessionUpdate.UserMessageChunk>(args.Update);
        Assert.Equal("What does this do?", chunk.Text);
    }

    [Fact]
    public async Task SetSessionConfigOptionAsync_UsesAuthoritativeModelDependentEffort_AndForwardsUpdates()
    {
        var received = new TaskCompletionSource<SessionUpdateEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.SessionUpdate += (_, update) => received.TrySetResult(update);

        Task<IReadOnlyList<SessionConfigOption>> pending =
            _connection.SetSessionConfigOptionAsync("s1", "model", "opus", CancellationToken.None);
        JsonObject request = await ReadRequestAsync("session/set_config_option");
        Assert.Equal("s1", request["params"]!["sessionId"]!.GetValue<string>());
        Assert.Equal("model", request["params"]!["configId"]!.GetValue<string>());
        Assert.Equal("opus", request["params"]!["value"]!.GetValue<string>());
        string changed = JsonNode.Parse("""
            [
              {"id":"model","name":"Model","category":"model","type":"select","currentValue":"opus",
               "options":[{"value":"opus","name":"Opus"}]},
              {"id":"effort","name":"Effort","category":"thought_level","type":"select","currentValue":"xhigh",
               "options":[{"value":"default","name":"Default"},{"value":"xhigh","name":"Extra High"}]}
            ]
            """)!.ToJsonString();
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"config_option_update","configOptions":"""
            + changed + "}}}");
        await ReplyAsync(request, """{"configOptions":""" + changed + "}");

        IReadOnlyList<SessionConfigOption> result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("opus", result[0].CurrentValue);
        Assert.Equal("xhigh", result[1].CurrentValue);
        Assert.Collection(result[1].Options,
            value => Assert.Equal("default", value.Value),
            value => Assert.Equal("xhigh", value.Value));
        SessionUpdateEventArgs notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("s1", notification.SessionId);
        var update = Assert.IsType<SessionUpdate.ConfigOptionsChanged>(notification.Update);
        Assert.Equal("xhigh", update.ConfigOptions[1].CurrentValue);

        pending = _connection.SetSessionConfigOptionAsync("s1", "effort", "default", CancellationToken.None);
        request = await ReadRequestAsync("session/set_config_option");
        Assert.Equal("effort", request["params"]!["configId"]!.GetValue<string>());
        Assert.Equal("default", request["params"]!["value"]!.GetValue<string>());
        await ReplyAsync(request, """{"configOptions":""" + changed.Replace("\"currentValue\":\"xhigh\"", "\"currentValue\":\"default\"") + "}");
        result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("opus", result[0].CurrentValue);
        Assert.Equal("default", result[1].CurrentValue);
    }

    [Fact]
    public async Task SetSessionConfigOptionAsync_MissingAuthoritativeOptions_DisconnectsExactlyOnce()
    {
        int disconnected = 0;
        _connection.Disconnected += (_, _) => Interlocked.Increment(ref disconnected);
        Task<IReadOnlyList<SessionConfigOption>> pending =
            _connection.SetSessionConfigOptionAsync("s1", "model", "sonnet", CancellationToken.None);
        JsonObject request = await ReadRequestAsync("session/set_config_option");
        await ReplyAsync(request, "{}");
        await Assert.ThrowsAsync<AcpProtocolException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await _connection.DisposeAsync();
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SetSessionConfigOptionAsync_CancelledInFlight_DisconnectsExactlyOnce()
    {
        int disconnected = 0;
        _connection.Disconnected += (_, _) => Interlocked.Increment(ref disconnected);
        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyList<SessionConfigOption>> pending =
            _connection.SetSessionConfigOptionAsync("s1", "model", "opus", cancellation.Token);
        await ReadRequestAsync("session/set_config_option");
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await _connection.DisposeAsync();
        Assert.Equal(1, disconnected);
        await Assert.ThrowsAnyAsync<Exception>(() => _connection.SendPromptAsync("s1",
            new ContentBlock[] { new ContentBlock.Text("must not send on stale session") }, CancellationToken.None));
    }

    [Fact]
    public async Task SetSessionConfigOptionAsync_ExplicitRejection_KeepsSessionUsable()
    {
        int disconnected = 0;
        _connection.Disconnected += (_, _) => Interlocked.Increment(ref disconnected);
        Task<IReadOnlyList<SessionConfigOption>> pending =
            _connection.SetSessionConfigOptionAsync("s1", "effort", "unavailable", CancellationToken.None);
        JsonObject request = await ReadRequestAsync("session/set_config_option");
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            "{\"jsonrpc\":\"2.0\",\"id\":" + request["id"]!.ToJsonString()
            + ",\"error\":{\"code\":-32602,\"message\":\"Invalid config value\"}}");
        await Assert.ThrowsAsync<AcpRemoteException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        Task prompt = _connection.SendPromptAsync("s1",
            new ContentBlock[] { new ContentBlock.Text("continue with acknowledged settings") }, CancellationToken.None);
        request = await ReadRequestAsync("session/prompt");
        await ReplyAsync(request, """{"stopReason":"end_turn"}""");
        await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, disconnected);
    }

    [Fact]
    public async Task SendPromptAsync_PreservesImageOnlyContent_AndCompletesTheTurn()
    {
        const string imageData = "iVBORw0KGgo=";
        var received = new TaskCompletionSource<SessionUpdateEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.SessionUpdate += (_, update) => received.TrySetResult(update);
        Task pending = _connection.SendPromptAsync("s1",
            new ContentBlock[] { new ContentBlock.Image("image/png", imageData) }, CancellationToken.None);
        JsonObject request = await ReadRequestAsync("session/prompt");
        JsonNode? image = Assert.Single(request["params"]!["prompt"]!.AsArray());
        Assert.Equal("image", image!["type"]!.GetValue<string>());
        Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());
        Assert.Equal(imageData, image["data"]!.GetValue<string>());
        await ReplyAsync(request, """{"stopReason":"end_turn"}""");
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        SessionUpdateEventArgs notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("end_turn", Assert.IsType<SessionUpdate.TurnEnded>(notification.Update).StopReason);
    }

    [Fact]
    public async Task SendPromptAsync_PreservesSlashCommandAndMultipleUnsavedResourcesWithImage()
    {
        Task pending = _connection.SendPromptAsync("s1", new ContentBlock[]
        {
            new ContentBlock.Text("/Plugin:review-code focus on unsaved changes"),
            new ContentBlock.EmbeddedTextResource("file:///D:/work/First%20File.cs", "var title = \"Zażółć\";\r\n// unsaved first buffer\r\n"),
            new ContentBlock.EmbeddedTextResource("file:///D:/work/Second.cs", "// unsaved second buffer\n<node value=\"&\" />", mimeType: null),
            new ContentBlock.Image("image/png", "iVBORw0KGgo="),
        }, CancellationToken.None);

        JsonObject request = await ReadRequestAsync("session/prompt");
        JsonNode expected = JsonNode.Parse("""
            {
              "sessionId":"s1",
              "prompt":[
                {"type":"text","text":"/Plugin:review-code focus on unsaved changes"},
                {"type":"resource","resource":{"uri":"file:///D:/work/First%20File.cs","text":"var title = \"Zażółć\";\r\n// unsaved first buffer\r\n","mimeType":"text/plain"}},
                {"type":"resource","resource":{"uri":"file:///D:/work/Second.cs","text":"// unsaved second buffer\n<node value=\"&\" />"}},
                {"type":"image","mimeType":"image/png","data":"iVBORw0KGgo="}
              ]
            }
            """)!;
        Assert.True(JsonNode.DeepEquals(expected, request["params"]),
            "Prompt payload must retain the standalone slash text, exact embedded snapshots, optional MIME omission, and image in order.");

        await ReplyAsync(request, """{"stopReason":"end_turn"}""");
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SessionUpdate_UsageUpdateWithOutOfRangeNumbers_DegradesThoseFieldsAndKeepsThePumpAlive()
    {
        // `cost.amount` is an unbounded JSON double on the wire; (decimal)1e29 throws OverflowException
        // inline on the JSON-RPC read pump, which would exit the read loop and fault every in-flight
        // request. `used`/`size` are uint64 there, so an unchecked (long) cast wraps to a negative or
        // garbage token count. Every one of those must degrade the field, not the connection.
        var received = new TaskCompletionSource<List<SessionUpdateEventArgs>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<SessionUpdateEventArgs>();
        _connection.SessionUpdate += (_, update) =>
        {
            notifications.Add(update);
            if (notifications.Count == 3) received.TrySetResult(notifications);
        };

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"usage_update","used":1e30,"size":18446744073709551615,"cost":{"amount":1e29,"currency":"USD"}}}}""");
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"usage_update","used":-5,"size":-1,"cost":{"amount":-1e29}}}}""");
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"usage_update","used":42,"size":100,"cost":{"amount":0.5,"currency":"USD"}}}}""");

        List<SessionUpdateEventArgs> updates = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saturated = Assert.IsType<SessionUpdate.UsageUpdate>(updates[0].Update);
        Assert.Equal(long.MaxValue, saturated.UsedTokens);
        Assert.Equal(long.MaxValue, saturated.ContextWindowSize);
        Assert.Null(saturated.CostAmount);
        var negative = Assert.IsType<SessionUpdate.UsageUpdate>(updates[1].Update);
        Assert.Equal(0, negative.UsedTokens);
        Assert.Equal(0, negative.ContextWindowSize);
        Assert.Null(negative.CostAmount);

        // The third notification only arrives if the pump survived the first two.
        var healthy = Assert.IsType<SessionUpdate.UsageUpdate>(updates[2].Update);
        Assert.Equal(42, healthy.UsedTokens);
        Assert.Equal(0.5m, healthy.CostAmount);
    }

    [Fact]
    public async Task SetRemoteControlAsync_Enabling_SendsSessionNameAndMapsTheAcknowledgedState()
    {
        Task<RemoteControlState> pending = _connection.SetRemoteControlAsync("s1", enabled: true, "My laptop", CancellationToken.None);
        JsonObject request = await ReadRequestAsync("_vs/remoteControl");
        Assert.Equal("s1", request["params"]!["sessionId"]!.GetValue<string>());
        Assert.True(request["params"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("My laptop", request["params"]!["name"]!.GetValue<string>());

        await ReplyAsync(request, """{"enabled":true,"sessionUrl":"https://claude.ai/code/s1","connectUrl":"https://claude.ai/code/connect"}""");

        RemoteControlState state = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.Enabled);
        Assert.Equal("https://claude.ai/code/s1", state.SessionUrl);
        Assert.Equal("https://claude.ai/code/connect", state.ConnectUrl);
    }

    [Fact]
    public async Task SetRemoteControlAsync_WithoutASessionName_OmitsNameAndReportsTheAgentsOwnState()
    {
        Task<RemoteControlState> pending = _connection.SetRemoteControlAsync("s1", enabled: true, "   ", CancellationToken.None);
        JsonObject request = await ReadRequestAsync("_vs/remoteControl");
        Assert.Null(request["params"]!["name"]);

        // The agent is authoritative: it refused to enable, so the UI must not show Remote Control as on.
        await ReplyAsync(request, """{"enabled":false}""");

        RemoteControlState state = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(state.Enabled);
        Assert.Null(state.SessionUrl);
    }

    [Fact]
    public async Task SetRemoteControlAsync_ResponseOmitsEnabled_FailsInsteadOfAssumingTheToggleApplied()
    {
        // An unacknowledged toggle must not be reported as success: the user would believe the session
        // is (or is no longer) exposed at claude.ai/code when the agent never said so.
        Task<RemoteControlState> pending = _connection.SetRemoteControlAsync("s1", enabled: true, null, CancellationToken.None);
        JsonObject request = await ReadRequestAsync("_vs/remoteControl");
        await ReplyAsync(request, """{"sessionUrl":"https://claude.ai/code/s1"}""");

        await Assert.ThrowsAsync<AcpProtocolException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task HandleCreateElicitationAsync_CancelledWhilePending_UnblocksInsteadOfHangingForever()
    {
        var handlerInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.ElicitationRequested += (_, _) => handlerInvoked.TrySetResult(true); // deliberately never resolves e.Response.

        using var cts = new CancellationTokenSource();
        MethodInfo method = typeof(AcpProcessConnection).GetMethod("HandleCreateElicitationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var paramsObj = new JsonObject
        {
            ["mode"] = "form",
            ["sessionId"] = "s1",
            ["message"] = "Pick one",
            ["requestedSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        };
        var resultTask = (Task<JsonNode?>)method.Invoke(_connection, new object[] { paramsObj, cts.Token })!;

        await handlerInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resultTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InboundElicitationCreateRequest_AgentDisconnectsWhilePending_FaultsTheUnansweredForm()
    {
        ElicitationRequestEventArgs? captured = null;
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.ElicitationRequested += (_, e) => { captured = e; received.TrySetResult(true); };

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, JsonNode.Parse("""
            {"jsonrpc":"2.0","id":26,"method":"elicitation/create","params":{
              "mode":"form","sessionId":"s1","message":"Pick one",
              "requestedSchema":{"type":"object","properties":{
                "question_0":{"type":"string","oneOf":[{"const":"red","title":"Red"}]}
              }}
            }}
            """)!.ToJsonString());
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The agent's stdout closes - the form can never be answered, so it must not stay open forever.
        _fromAgent.Writer.Complete();

        // A TimeoutException from WaitAsync would mean the form is still pending - the exact bug
        // this covers - so the disconnect cause itself has to be the assertion.
        await Assert.ThrowsAsync<IOException>(() => captured!.Response.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InboundElicitationCreateRequest_MissingRequestedSchema_RespondsWithAnErrorAndKeepsThePumpAlive()
    {
        _connection.ElicitationRequested += (_, e) =>
            e.Response.TrySetResult(new ElicitationAnswer(ElicitationAction.Accept, new Dictionary<string, IReadOnlyList<string>>()));

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","id":27,"method":"elicitation/create","params":{"mode":"form","sessionId":"s1","message":"Pick one"}}""");

        JsonObject response = await ReadResponseWithIdAsync(_toAgent.Reader, 27);
        Assert.Null(response["result"]);
        Assert.NotNull(response["error"]);

        // The pump must still be serving requests afterwards.
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, JsonNode.Parse("""
            {"jsonrpc":"2.0","id":28,"method":"elicitation/create","params":{
              "mode":"form","sessionId":"s1","message":"Pick one",
              "requestedSchema":{"type":"object","properties":{}}
            }}
            """)!.ToJsonString());
        JsonObject accepted = await ReadResponseWithIdAsync(_toAgent.Reader, 28);
        Assert.Equal("accept", accepted["result"]!["action"]!.GetValue<string>());
    }

    [Fact]
    public async Task SessionUpdate_ToolCallWithoutAToolCallId_DropsThatNotificationAndKeepsThePumpAlive()
    {
        // ParseToolCallUpdate requires `toolCallId` and runs inline on the JSON-RPC read pump, so an
        // escaping AcpProtocolException ends the read loop and faults every in-flight request - one
        // malformed message from the untrusted agent would kill the whole session.
        var received = new TaskCompletionSource<SessionUpdateEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<SessionUpdateEventArgs>();
        _connection.SessionUpdate += (_, update) => { seen.Add(update); received.TrySetResult(update); };

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"tool_call","title":"Read a.cs"}}}""");
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"still here"}}}}""");

        SessionUpdateEventArgs survivor = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var chunk = Assert.IsType<SessionUpdate.AgentMessageChunk>(survivor.Update);
        Assert.Equal("still here", chunk.Text);
        Assert.Single(seen); // the malformed tool_call was dropped, not surfaced as a default-filled call.
    }

    [Fact]
    public async Task SessionUpdate_UsageUpdateWithANonNumericUsed_DropsTheUpdateInsteadOfReportingZeroTokens()
    {
        // "unknown" is not "0 used": reporting zero would draw an empty context-window bar for a
        // window that may be nearly full.
        var received = new TaskCompletionSource<SessionUpdateEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<SessionUpdateEventArgs>();
        _connection.SessionUpdate += (_, update) => { seen.Add(update); received.TrySetResult(update); };

        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"usage_update","used":"lots","size":200000}}}""");
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"usage_update","used":42,"size":200000}}}""");

        SessionUpdateEventArgs survivor = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var usage = Assert.IsType<SessionUpdate.UsageUpdate>(survivor.Update);
        Assert.Equal(42, usage.UsedTokens);
        Assert.Single(seen);
    }

    [Fact]
    public async Task ListSessionsAsync_RowMissingRequiredFields_SkipsThatRowInsteadOfDiscardingTheHistory()
    {
        Task<IReadOnlyList<SessionSummary>> pending = _connection.ListSessionsAsync(null, CancellationToken.None);
        JsonObject request = await ReadRequestAsync("session/list");
        // `cwd` is optional and nullable in ACP's ListSessionsRequest; sending an explicit null for
        // an unfiltered list is equivalent to omitting the key, so neither form is asserted here.

        await ReplyAsync(request, """
            {
              "sessions": [
                { "cwd": "/workspace", "title": "No session id" },
                { "sessionId": "s2", "title": "No cwd" },
                "not-an-object",
                { "sessionId": "s4", "cwd": "/workspace", "title": "Fix the bug" }
              ]
            }
            """);

        IReadOnlyList<SessionSummary> result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        SessionSummary only = Assert.Single(result);
        Assert.Equal("s4", only.SessionId);
    }

    private async Task<JsonObject> ReadRequestAsync(string expectedMethod)
    {
        string line = await PipeTestHelpers.ReadLineAsync(_toAgent.Reader).WaitAsync(TimeSpan.FromSeconds(5));
        JsonObject request = JsonNode.Parse(line)!.AsObject();
        Assert.Equal(expectedMethod, request["method"]!.GetValue<string>());
        return request;
    }

    private Task ReplyAsync(JsonObject request, string result) =>
        PipeTestHelpers.WriteLineAsync(_fromAgent.Writer, "{\"jsonrpc\":\"2.0\",\"id\":" + request["id"]!.ToJsonString() + ",\"result\":" + JsonNode.Parse(result)!.ToJsonString() + "}");

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
