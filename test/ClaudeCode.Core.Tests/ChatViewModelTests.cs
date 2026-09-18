using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChatViewModelTests
{
    // F-7-15: NullChatSessionServices + FakeAcpAgentConnection/FakeAcpAgentConnectionFactory (in
    // ViewModels/Demo/) are a live production fallback (ChatPanelView.xaml.cs falls back to
    // NullChatSessionServices when no host-provided IChatSessionServices is available) that had no
    // test coverage. This asserts the combination is wired correctly end to end: sending a prompt
    // produces a visible assistant reply. It intentionally does not assert the exact echo wording,
    // which is an implementation detail of the demo double, not an observable contract.
    [Fact]
    public async Task SendAsync_WithNullChatSessionServices_ProducesAssistantMessage()
    {
        using var vm = new ChatViewModel(new ClaudeCode.Core.ViewModels.Demo.NullChatSessionServices());
        await vm.InitializeAsync();

        vm.InputText = "hello there";
        await vm.SendAsync();

        var assistantMessage = Assert.Single(vm.Messages, m => m.Role == ChatRole.Assistant);
        Assert.False(string.IsNullOrWhiteSpace(assistantMessage.Text));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task PlanSessionUpdate_ReplacesPreviousPlan_AndReleaseConnectionClearsIt()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "make a plan";
        await vm.SendAsync();

        connection.RaiseSessionUpdate(new SessionUpdate.Plan(new List<PlanEntry>
        {
            new PlanEntry { Content = "Write tests", Status = PlanEntryStatus.Pending },
        }));
        Assert.Equal("Write tests", Assert.Single(vm.CurrentPlan!.Entries).Content);

        // A later plan update replaces the prior one wholesale; it does not merge/append entries.
        connection.RaiseSessionUpdate(new SessionUpdate.Plan(new List<PlanEntry>
        {
            new PlanEntry { Content = "Ship feature", Status = PlanEntryStatus.InProgress },
        }));
        Assert.Equal("Ship feature", Assert.Single(vm.CurrentPlan!.Entries).Content);

        connection.RaiseDisconnected();
        Assert.Null(vm.CurrentPlan);
    }

    [Fact]
    public async Task PermissionRequested_ChoosingSecondOption_RespondsWithSecondOptionId()
    {
        var connection = new RecordingAcpAgentConnection();
        var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "edit a file for me";
        await vm.SendAsync();

        var call = new ToolCallUpdate { ToolCallId = "tc-1", Title = "Edit file.txt", Status = ToolCallStatus.Pending };
        var options = new List<PermissionOption>
        {
            new PermissionOption { OptionId = "allow-once", Label = "Allow", Outcome = PermissionOutcome.AllowOnce },
            new PermissionOption { OptionId = "reject-once", Label = "Reject", Outcome = PermissionOutcome.RejectOnce },
        };

        var requestArgs = connection.RaisePermissionRequested(call, options);

        Assert.NotNull(vm.PendingPermission);
        var secondOption = vm.PendingPermission!.Options[1];
        vm.PendingPermission.ChooseCommand.Execute(secondOption);

        var resultOptionId = await requestArgs.Response.Task;

        Assert.Equal("reject-once", resultOptionId);
        Assert.Null(vm.PendingPermission);
    }

    [Fact]
    public async Task NewSessionCommand_StartsFreshSessionOnSameConnection_AndClearsTranscript()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "hello";
        await vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("hi"));
        Assert.NotEmpty(vm.Messages);

        var callsBefore = connection.DisposeCount;
        var newSessionCalls = 0;
        connection.NewSessionHandler = _ => { newSessionCalls++; return Task.FromResult(new NewSessionResult("session-2", [])); };

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Equal(1, newSessionCalls);
        Assert.Equal(callsBefore, connection.DisposeCount); // the ACP process/connection is reused, not torn down.
        Assert.Empty(vm.Messages);
        Assert.Null(vm.CurrentPlan);
    }

    [Fact]
    public async Task ShowHistoryCommand_PopulatesSessionHistory_ScopedToWorkspaceRoot()
    {
        var connection = new RecordingAcpAgentConnection
        {
            ListSessionsHandler = (cwd, _) => Task.FromResult<IReadOnlyList<SessionSummary>>(
            [
                new SessionSummary("s1", cwd!, "Fix the bug", null),
                new SessionSummary("s2", cwd!, null, null),
            ]),
        };
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), "/workspace"));
        await vm.InitializeAsync();

        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.Equal(["/workspace"], connection.ListSessionsCwds);
        Assert.Equal(2, vm.SessionHistory.Count);
        Assert.Equal("Fix the bug", vm.SessionHistory[0].Title);
        Assert.True(vm.IsHistoryOpen);
    }

    [Fact]
    public async Task OpenSessionCommand_LoadsSession_ClearsTranscript_AndRebuildsFromReplayedUpdates()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), "/workspace"));
        await vm.InitializeAsync();

        vm.InputText = "hello";
        await vm.SendAsync();
        Assert.NotEmpty(vm.Messages);

        var target = new SessionSummary("session-old", "/workspace", "Older chat", null);
        connection.LoadSessionHandler = (sessionId, cwd, mcpServers, _) =>
        {
            // The agent replays history as ordinary session/update notifications before the
            // session/load response resolves.
            connection.RaiseSessionUpdate(new SessionUpdate.UserMessageChunk("What does this do?"), sessionId);
            connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("It does X."), sessionId);
            return Task.FromResult(new NewSessionResult(sessionId, []));
        };

        await vm.OpenSessionCommand.ExecuteAsync(target);

        Assert.Equal([("session-old", "/workspace")], connection.LoadedSessions);
        Assert.Collection(vm.Messages,
            user =>
            {
                Assert.Equal(ChatRole.User, user.Role);
                Assert.Equal("What does this do?", user.Text);
            },
            assistant =>
            {
                Assert.Equal(ChatRole.Assistant, assistant.Role);
                Assert.Equal("It does X.", assistant.Text);
            });
        Assert.False(vm.IsHistoryOpen);
    }

    [Fact]
    public async Task ElicitationRequested_Submit_RespondsWithAcceptedAnswer_AndClearsPendingElicitation()
    {
        var connection = new RecordingAcpAgentConnection();
        var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "ask me something";
        await vm.SendAsync();

        var field = new ElicitationField("q0", "Color", null, ElicitationFieldKind.SingleSelect,
        [
            new ElicitationOption("red", "Red"),
            new ElicitationOption("blue", "Blue"),
        ]);
        var requestArgs = connection.RaiseElicitationRequested("Pick a color", [field]);

        Assert.NotNull(vm.PendingElicitation);
        vm.PendingElicitation!.Fields[0].Options[1].IsSelected = true;
        vm.PendingElicitation.SubmitCommand.Execute(null);

        var answer = await requestArgs.Response.Task;

        Assert.Equal(ElicitationAction.Accept, answer.Action);
        Assert.Equal(new[] { "blue" }, answer.Content["q0"]);
        Assert.Null(vm.PendingElicitation);
    }

    [Fact]
    public async Task NewSessionCommand_ResolvesStillPendingElicitationAsCancelled()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "ask me something";
        await vm.SendAsync();
        var requestArgs = connection.RaiseElicitationRequested("Pick a color", [new ElicitationField("q0", null, null, ElicitationFieldKind.Text, [])]);
        Assert.NotNull(vm.PendingElicitation);

        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult("session-2", []));
        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Null(vm.PendingElicitation);
        ElicitationAnswer answer = await requestArgs.Response.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ElicitationAction.Cancel, answer.Action);
    }
}
