using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
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
    public async Task InboundElicitationCreateRequest_UnsupportedMode_DeclinesImmediately()
    {
        await PipeTestHelpers.WriteLineAsync(_fromAgent.Writer,
            """{"jsonrpc":"2.0","id":22,"method":"elicitation/create","params":{"mode":"url","sessionId":"s1","message":"Open this","url":"https://example.test"}}""");

        JsonObject response = await ReadResponseWithIdAsync(_toAgent.Reader, 22);
        Assert.Equal("decline", response["result"]!["action"]!.GetValue<string>());
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
