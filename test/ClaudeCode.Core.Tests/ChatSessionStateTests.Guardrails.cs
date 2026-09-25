using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed partial class ChatSessionStateTests
{
    // M12 (permission-request-overwritten): a second permission request arriving while the first
    // is still pending must not silently abandon the first — it is explicitly rejected.
    [Fact]
    public async Task SecondPermissionRequest_DoesNotSilentlyAbandonTheFirst()
    {
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => new TaskCompletionSource<bool>().Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "do two things";
        _ = vm.SendAsync();

        var firstCall = new ToolCallUpdate { ToolCallId = "tc-1", Title = "Edit a.cs", Status = ToolCallStatus.Pending };
        var firstOptions = new List<PermissionOption> { new PermissionOption { OptionId = "allow-a", Label = "Allow", Outcome = PermissionOutcome.AllowOnce } };
        var first = connection.RaisePermissionRequested(firstCall, firstOptions);

        var secondCall = new ToolCallUpdate { ToolCallId = "tc-2", Title = "Edit b.cs", Status = ToolCallStatus.Pending };
        var secondOptions = new List<PermissionOption> { new PermissionOption { OptionId = "allow-b", Label = "Allow", Outcome = PermissionOutcome.AllowOnce } };
        var second = connection.RaisePermissionRequested(secondCall, secondOptions);

        // The first request must be resolved (not left hanging), and the second must remain live.
        await Assert.ThrowsAsync<OperationCanceledException>(() => first.Response.Task);
        Assert.Equal("Edit b.cs", vm.PendingPermission!.Title);

        vm.PendingPermission.ChooseCommand.Execute(vm.PendingPermission.Options[0]);
        Assert.Equal("allow-b", await second.Response.Task);
    }

    // stop-button-stays-enabled: the cancel button must not remain enabled once the connection that
    // owned the in-flight turn is gone.
    [Fact]
    public async Task CancelCommand_DisconnectedDuringStream_BecomesDisabled()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "start a long turn";
        var prompt = vm.SendAsync();

        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelCommand.CanExecute(null));

        connection.RaiseDisconnected();

        Assert.False(vm.CancelCommand.CanExecute(null));

        completed.SetResult(true);
        await prompt;
    }

    [Fact]
    public async Task CancelCommand_DuringStreamingTurn_SendsCancel()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "start a long turn";
        var prompt = vm.SendAsync();

        Assert.True(vm.CancelCommand.CanExecute(null));
        await vm.CancelCommand.ExecuteAsync(null);

        Assert.Equal(1, connection.CancelCount);

        completed.SetResult(true);
        await prompt;
    }

    // sync-context-silent-inline: constructing a ChatViewModel off a UI-affine thread must fail fast
    // instead of silently degrading RunOnUi/OnUiAsync to always-inline execution.
    [Fact]
    public async Task Constructor_WithoutAmbientSynchronizationContext_ThrowsInvalidOperationException()
    {
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService());

        var thrown = await Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            return Record.Exception(() => new ChatViewModel(services));
        });

        Assert.IsType<InvalidOperationException>(thrown);
    }

    // attachment-size-unbounded: oversized attachments are rejected with a visible error instead of
    // being queued for an unbounded prompt payload.
    [Fact]
    public async Task AddImageAttachment_ExceedsFiveMegabyteLimit_SetsErrorAndDoesNotAttach()
    {
        using var vm = Create(new RecordingAcpAgentConnection());
        await vm.Initialization;
        var oversizedBase64 = new string('A', 7_000_000);

        vm.AddImageAttachment("big.png", "image/png", oversizedBase64);

        Assert.Empty(vm.Attachments);
        Assert.Equal("Image exceeds the 5 MB attachment limit.", vm.AttachmentError);
    }

    [Fact]
    public async Task AttachActiveDocument_ExceedsOneMegabyteLimit_SetsErrorAndDoesNotAttach()
    {
        var connection = new RecordingAcpAgentConnection();
        var oversizedText = new string('a', 1_100_000);
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            CaptureHandler = _ => Task.FromResult<EditorDocumentSnapshot?>(new EditorDocumentSnapshot(@"C:\Workspace\huge.cs", oversizedText)),
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;

        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);

        Assert.Empty(vm.Attachments);
        Assert.Equal("Document exceeds the 1 MB attachment limit.", vm.AttachmentError);
    }

    // dispose-cts-not-released: disposing twice must stay idempotent even though the second call now
    // also has to tolerate the CancellationTokenSource/SemaphoreSlim already being disposed.
    [Fact]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        var connection = new RecordingAcpAgentConnection();
        var vm = Create(connection);
        await vm.Initialization;

        vm.Dispose();
        var ex = Record.Exception(() => vm.Dispose());

        Assert.Null(ex);
    }

    // pending-state-leaks-on-release: losing the connection must resolve every request the user was
    // still being asked about — an abandoned TaskCompletionSourceSlot never completes its awaiter,
    // and a form left on screen would answer a connection that no longer exists.
    [Fact]
    public async Task Disconnected_ResolvesPendingPermissionAndElicitation_AndClearsTheirUi()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;

        var call = new ToolCallUpdate { ToolCallId = "tc-1", Title = "Edit a.cs", Status = ToolCallStatus.Pending };
        var permission = connection.RaisePermissionRequested(call,
            [new PermissionOption { OptionId = "allow", Label = "Allow", Outcome = PermissionOutcome.AllowOnce }]);
        var elicitation = connection.RaiseElicitationRequested("Pick a color",
            [new ElicitationField("q0", null, null, ElicitationFieldKind.Text, [])]);
        Assert.NotNull(vm.PendingPermission);
        Assert.NotNull(vm.PendingElicitation);

        connection.RaiseDisconnected();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => permission.Response.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var answer = await elicitation.Response.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ElicitationAction.Cancel, answer.Action);
        Assert.Null(vm.PendingPermission);
        Assert.Null(vm.PendingElicitation);
    }

    // poisoned-session-id-after-failed-load: a session/load that fails must not leave the viewmodel
    // believing it owns a session the agent never loaded.
    [Fact]
    public async Task OpenSession_LoadFails_DoesNotAdoptTheSessionTheAgentNeverLoaded()
    {
        var connection = new RecordingAcpAgentConnection
        {
            LoadSessionHandler = (_, _, _, _) =>
                Task.FromException<NewSessionResult>(new InvalidOperationException("Session not found")),
        };
        using var vm = Create(connection);
        await vm.Initialization;

        await vm.OpenSessionAsync(new SessionSummary("session-never-loaded", "/workspace", "Older chat", null));

        Assert.Contains("Could not open session", vm.StatusMessage!, StringComparison.Ordinal);
        // The presented state must roll back with the id: the panel must not be left titled after a
        // session the agent never loaded while every prompt still goes to the previous one.
        Assert.Equal("Untitled", vm.SessionTitle);

        // Updates tagged with the session that failed to load must not be adopted as the transcript.
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("ghost text"), "session-never-loaded");
        Assert.Empty(vm.Messages);

        // The session the agent really has must still drive the transcript, so the user can keep
        // working (and send prompts) without manually starting a new session.
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("live text"));
        Assert.Equal("live text", Assert.Single(vm.Messages).Text);
        vm.InputText = "carry on";
        await vm.SendAsync();
        Assert.Single(connection.Prompts);
    }

    // The rollback puts the user back in the conversation they were in, so everything
    // ResetTranscriptState destroyed on the way into the load has to come back with it: a pending
    // Accept/Reject row whose edit is still on disk, the context ring, and the slash catalog.
    [Fact]
    public async Task OpenSession_LoadFails_RestoresTheTranscriptItClearedBeforeTheLoad()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Tracked.cs");
        File.WriteAllText(targetPath, "original\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        vm.InputText = "what does this do?";
        await vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("it does this"));
        connection.RaiseSessionUpdate(new SessionUpdate.UsageUpdate(12_000, 200_000, null, null));
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Review", "scope")]));
        Assert.True(await connection.RaiseFileWriteRequested(targetPath, "agent edit\n").Response.Task);
        var before = vm.Messages.ToList();
        Assert.Equal(2, before.Count);
        connection.LoadSessionHandler = (_, _, _, _) =>
            Task.FromException<NewSessionResult>(new InvalidOperationException("Agent restarted"));

        await vm.OpenSessionAsync(new SessionSummary("session-2", workspace.Root, "Older chat", null));

        Assert.Equal(before, vm.Messages);
        Assert.Equal("what does this do?", vm.SessionTitle);
        Assert.Equal(12_000, vm.SessionUsedTokens);
        Assert.Equal(200_000, vm.ContextWindowSize);
        Assert.Equal(6, vm.ContextUsagePercent);
        vm.InputText = "/";
        Assert.Equal(new[] { "review" }.Concat(ClientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
        vm.InputText = string.Empty;

        // The agent's edit is still on disk, so the row that offers to revert it must survive too.
        var tracked = Assert.Single(vm.ChangedFiles);
        await tracked.RejectCommand.ExecuteAsync(null);
        Assert.Equal("original\n", File.ReadAllText(targetPath));
    }

    // C-D3 (CRITICAL, agent-supplied cwd): SessionSummary.Cwd is copied verbatim out of the agent's
    // session/list reply and, passed to session/load, becomes the WorkspacePathGuard root of every
    // VS-control tool for the resumed session. Resuming must use the client's own workspace root.
    [Fact]
    public async Task OpenSession_ResumesWithTheClientsWorkspaceRoot_NotTheAgentReportedCwd()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(
            new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), @"C:\trusted\workspace"));
        await vm.Initialization;

        await vm.OpenSessionAsync(new SessionSummary("session-2", @"C:\", "Hostile", null));

        var loaded = Assert.Single(connection.LoadedSessions);
        Assert.Equal("session-2", loaded.SessionId);
        Assert.Equal(@"C:\trusted\workspace", loaded.Cwd);
    }

    // A prompt accepted while session/new or session/load is still in flight is sent to the *old*
    // session and then wiped by ResetTranscriptState, so the switch must gate the composer.
    [Fact]
    public async Task SessionSwitchInFlight_BlocksTheComposerAndASecondSwitch()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        using var vm = Create(connection);
        await vm.Initialization;
        var pending = new TaskCompletionSource<NewSessionResult>();
        connection.NewSessionHandler = _ => pending.Task;

        var switching = vm.NewSessionAsync();
        vm.InputText = "typed while switching";
        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.False(vm.NewSessionCommand.CanExecute(null));
        Assert.False(vm.ShowHistoryCommand.CanExecute(null));
        await vm.SendAsync();
        Assert.Empty(connection.Prompts);
        Assert.Empty(vm.Messages);

        pending.SetResult(new NewSessionResult("session-2", Options()));
        await switching;

        Assert.Equal("typed while switching", vm.InputText);
        Assert.True(vm.SendCommand.CanExecute(null));
        await vm.SendAsync();
        Assert.Single(connection.Prompts);
        Assert.Single(vm.Messages);
    }

    // The previous session's pickers stay populated through a session/load, and _sessionId is
    // already the incoming id so replayed updates are accepted. Without this gate a model change
    // or Remote Control toggle mid-replay addresses a session the agent has not finished loading -
    // and may roll back.
    [Fact]
    public async Task SessionLoadInFlight_DisablesSettingsAndRemoteControl()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        using var vm = Create(connection);
        await vm.Initialization;
        var pending = new TaskCompletionSource<NewSessionResult>();
        connection.LoadSessionHandler = (_, _, _, _) => pending.Task;

        var opening = vm.OpenSessionAsync(new SessionSummary("session-2", "/workspace", "Older chat", null));
        Assert.False(vm.CanConfigure);
        Assert.False(vm.ToggleRemoteControlCommand.CanExecute(null));
        await vm.SelectModelAsync(vm.AvailableModels[1]);
        Assert.Empty(connection.ConfigChanges);

        pending.SetResult(new NewSessionResult("session-2", Options()));
        await opening;

        Assert.True(vm.CanConfigure);
        Assert.True(vm.ToggleRemoteControlCommand.CanExecute(null));
    }

    // #40: session/new and session/load each make the agent start a fresh Claude Code process and
    // wait for it to load the user's settings and plugins, so they take seconds. The click must be
    // acknowledged at once instead of looking like it did nothing.
    [Fact]
    public async Task NewChat_ReportsProgressUntilTheAgentHasStartedTheSession()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        using var vm = Create(connection);
        await vm.Initialization;
        var pending = new TaskCompletionSource<NewSessionResult>();
        connection.NewSessionHandler = _ => pending.Task;

        var switching = vm.NewSessionAsync();
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));

        pending.SetResult(new NewSessionResult("session-2", Options()));
        await switching;
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task OpenSession_ReportsProgressUntilTheAgentHasLoadedTheSession()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        using var vm = Create(connection);
        await vm.Initialization;
        var pending = new TaskCompletionSource<NewSessionResult>();
        connection.LoadSessionHandler = (_, _, _, _) => pending.Task;

        var opening = vm.OpenSessionAsync(new SessionSummary("session-2", "/workspace", "Older chat", null));
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));

        pending.SetResult(new NewSessionResult("session-2", Options()));
        await opening;
        Assert.Null(vm.StatusMessage);
    }

    // Rapid clicks while a history item is still loading: each extra click would otherwise start
    // another session/load or session/new whose replies race to become the panel's session.
    [Fact]
    public async Task SessionLoadInFlight_FurtherOpenAndNewChatClicks_StartNoSecondSession()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        using var vm = Create(connection);
        await vm.Initialization;
        var pending = new TaskCompletionSource<NewSessionResult>();
        connection.LoadSessionHandler = (_, _, _, _) => pending.Task;

        var opening = vm.OpenSessionAsync(new SessionSummary("session-2", "/workspace", "Older chat", null));
        var secondOpen = vm.OpenSessionAsync(new SessionSummary("session-3", "/workspace", "Other chat", null));
        var newChat = vm.NewSessionAsync();

        pending.SetResult(new NewSessionResult("session-2", Options()));
        await Task.WhenAll(opening, secondOpen, newChat).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("session-2", Assert.Single(connection.LoadedSessions).SessionId);
        Assert.Single(connection.NewSessionCwds);
        Assert.Equal("Older chat", vm.SessionTitle);
    }

    // C-D3 for the other session path: session/new is issued on every connect and every New Chat,
    // and both must carry the client's own workspace root, never anything that came off the wire.
    [Fact]
    public async Task NewSession_UsesTheClientsWorkspaceRoot_OnTheInitialConnectAndOnNewChat()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(
            new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), @"C:\trusted\workspace"));
        await vm.Initialization;
        Assert.Equal(@"C:\trusted\workspace", Assert.Single(connection.NewSessionCwds));

        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult("session-2", []));
        await vm.NewSessionAsync();

        Assert.Equal(new[] { @"C:\trusted\workspace", @"C:\trusted\workspace" }, connection.NewSessionCwds);
    }

    // EnsureConnectedAsync creates a session of its own whenever it has to connect, so New Chat
    // from a disconnected state must adopt that one: a second session/new leaves the first
    // orphaned on the agent, and with RemoteControlAtStartup the orphan can be the session
    // published to claude.ai/code - enabled, invisible, and unreachable from a toggle that only
    // ever addresses the session the UI knows about.
    [Fact]
    public async Task NewChat_AfterADisconnect_AdoptsTheSessionTheReconnectCreated_InsteadOfOrphaningIt()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first prompt";
        await vm.SendAsync();
        Assert.NotEmpty(vm.Messages);
        connection.RaiseDisconnected();

        connection.NewSessionHandler = _ =>
        {
            // The reconnect's session/new publishes its catalog before its id is known, exactly as
            // the initial connect does; adopting the reconnect's session must not discard it.
            connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Review", "scope")]), "session-reconnected");
            return Task.FromResult(new NewSessionResult("session-reconnected", []));
        };
        await vm.NewSessionAsync();

        // One session/new for the initial connect, one for the reconnect - not three.
        Assert.Equal(2, connection.NewSessionCwds.Count);
        Assert.Empty(vm.Messages);
        Assert.Equal("Untitled", vm.SessionTitle);
        vm.InputText = "/";
        Assert.Equal(new[] { "review" }.Concat(ClientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
    }
}
