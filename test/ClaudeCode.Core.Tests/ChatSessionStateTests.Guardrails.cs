using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
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
}
