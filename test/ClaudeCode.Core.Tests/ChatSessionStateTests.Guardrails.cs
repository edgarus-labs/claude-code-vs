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

        await Assert.ThrowsAsync<OperationCanceledException>(() => first.Response.Task);
        Assert.Equal("Edit b.cs", vm.PendingPermission!.Title);

        vm.PendingPermission.ChooseCommand.Execute(vm.PendingPermission.Options[0]);
        Assert.Equal("allow-b", await second.Response.Task);
    }

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

    [Fact]
    public async Task Stop_WhenTheConnectionCancelsAPendingPermission_ItsCardLeavesTheChat()
    {
        var turn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => turn.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "edit it";
        var sending = vm.SendAsync();
        var permission = connection.RaisePermissionRequested(
            new ToolCallUpdate { ToolCallId = "tc-1", Title = "Edit a.cs", Status = ToolCallStatus.Pending },
            [new PermissionOption { OptionId = "allow", Label = "Allow", Outcome = PermissionOutcome.AllowOnce }]);
        connection.CancelHandler = () =>
        {
            permission.Response.TrySetResult("cancelled-by-connection");

            return Task.CompletedTask;
        };
        Assert.NotNull(vm.PendingPermission);

        await vm.CancelAsync();

        await WaitUntilAsync(() => vm.PendingPermission is null);
        Assert.Equal("cancelled-by-connection", await permission.Response.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        turn.SetResult(true);
        await sending;
    }

    [Fact]
    public async Task Stop_WhenTheConnectionCancelsAPendingElicitation_ItsFormLeavesTheChat()
    {
        var turn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => turn.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "ask me";
        var sending = vm.SendAsync();
        var elicitation = connection.RaiseElicitationRequested("Pick a color",
            [new ElicitationField("q0", null, null, ElicitationFieldKind.Text, [])]);
        connection.CancelHandler = () =>
        {
            elicitation.Response.TrySetResult(new ElicitationAnswer(ElicitationAction.Cancel, new Dictionary<string, IReadOnlyList<string>>()));

            return Task.CompletedTask;
        };
        Assert.NotNull(vm.PendingElicitation);

        await vm.CancelAsync();

        await WaitUntilAsync(() => vm.PendingElicitation is null);
        Assert.False(vm.IsElicitationOpen);

        turn.SetResult(true);
        await sending;
    }

    [Fact]
    public async Task Stop_WhenTheConnectionCancelsAPlanApproval_TheCardLeavesAndThePlanDocumentGoesDead()
    {
        var turn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => turn.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "plan it";
        var sending = vm.SendAsync();
        var (call, options) = PlanApprovalRequest();
        var permission = connection.RaisePermissionRequested(call, options);
        connection.CancelHandler = () =>
        {
            permission.Response.TrySetResult("cancelled-by-connection");

            return Task.CompletedTask;
        };
        var plan = vm.PendingPlan!;
        Assert.False(plan.IsResolved);

        await vm.CancelAsync();

        await WaitUntilAsync(() => vm.PendingPermission is null && plan.IsResolved);
        Assert.False(plan.ProceedCommand.CanExecute(null));
        Assert.False(plan.ReviewCommand.CanExecute("late comments"));

        turn.SetResult(true);
        await sending;
    }

    [Fact]
    public async Task PermissionRequest_EndingWithoutAnAnswerFromTheCard_RemovesItsCard()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        var permission = connection.RaisePermissionRequested(
            new ToolCallUpdate { ToolCallId = "tc-1", Title = "Edit a.cs", Status = ToolCallStatus.Pending },
            [new PermissionOption { OptionId = "allow", Label = "Allow", Outcome = PermissionOutcome.AllowOnce }]);
        Assert.NotNull(vm.PendingPermission);

        permission.Response.TrySetException(new IOException("transport closed"));

        await WaitUntilAsync(() => vm.PendingPermission is null);
    }

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
        Assert.Equal("Untitled", vm.SessionTitle);

        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("ghost text"), "session-never-loaded");
        Assert.Empty(vm.Messages);

        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("live text"));
        Assert.Equal("live text", Assert.Single(vm.Messages).Text);
        vm.InputText = "carry on";
        await vm.SendAsync();
        Assert.Single(connection.Prompts);
    }

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
        Assert.Equal(new[] { "review" }.Concat(_clientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
        vm.InputText = string.Empty;

        var tracked = Assert.Single(vm.ChangedFiles);
        await tracked.RejectCommand.ExecuteAsync(null);
        Assert.Equal("original\n", File.ReadAllText(targetPath));
    }

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
            connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Review", "scope")]), "session-reconnected");

            return Task.FromResult(new NewSessionResult("session-reconnected", []));
        };
        await vm.NewSessionAsync();

        Assert.Equal(2, connection.NewSessionCwds.Count);
        Assert.Empty(vm.Messages);
        Assert.Equal("Untitled", vm.SessionTitle);
        vm.InputText = "/";
        Assert.Equal(new[] { "review" }.Concat(_clientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
    }
}
