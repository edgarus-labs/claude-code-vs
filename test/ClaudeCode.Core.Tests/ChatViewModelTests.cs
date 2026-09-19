using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

        // Agents emit a plan update per todo transition, so a collapsed Tasks list must stay
        // collapsed across the swap - otherwise it pops back open several times per turn.
        vm.CurrentPlan!.IsExpanded = false;

        // A later plan update replaces the prior one wholesale; it does not merge/append entries.
        connection.RaiseSessionUpdate(new SessionUpdate.Plan(new List<PlanEntry>
        {
            new PlanEntry { Content = "Ship feature", Status = PlanEntryStatus.InProgress },
        }));
        Assert.Equal("Ship feature", Assert.Single(vm.CurrentPlan!.Entries).Content);
        Assert.False(vm.CurrentPlan.IsExpanded);

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

    // The permission prompt is the one place the user has to decide whether to let the agent act,
    // and MCP tools reach it as a routing identifier (mcp__visual-studio__listAppWindows). Nobody
    // can consent to that string, so both the prompt and the transcript card must read as English.
    [Fact]
    public async Task PermissionRequested_ForAnMcpTool_AsksInWordsRatherThanARoutingIdentifier()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "look at my windows";
        await vm.SendAsync();

        var call = new ToolCallUpdate
        {
            ToolCallId = "tc-mcp",
            Title = "mcp__visual-studio__listAppWindows",
            Status = ToolCallStatus.Pending,
        };
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(call));
        connection.RaisePermissionRequested(call,
            [new PermissionOption { OptionId = "allow-once", Label = "Allow", Outcome = PermissionOutcome.AllowOnce }]);

        // The wording itself belongs to ToolDisplayNameTests; what this path owns is that neither
        // consent surface can present the raw routing identifier as the thing being approved.
        Assert.DoesNotContain("mcp__", vm.PendingPermission!.Title, StringComparison.Ordinal);
        Assert.Contains("windows", vm.PendingPermission.Title, StringComparison.OrdinalIgnoreCase);
        var card = Assert.Single(vm.Messages.SelectMany(message => message.ToolCalls), tool => tool.ToolCallId == "tc-mcp");
        Assert.Equal(vm.PendingPermission.Title, card.Title);
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

    private static (ToolCallUpdate Call, List<PermissionOption> Options) PlanApprovalRequest(string plan) =>
        (new ToolCallUpdate
        {
            ToolCallId = "plan-1",
            Title = "Approve Plan",
            Kind = "switch_mode",
            Status = ToolCallStatus.Pending,
            Content = [new ToolCallContent { Text = plan }],
        },
        [
            new PermissionOption { OptionId = "allow-once", Label = "Yes, proceed", Outcome = PermissionOutcome.AllowOnce },
            new PermissionOption { OptionId = "reject-once", Label = "No, keep planning", Outcome = PermissionOutcome.RejectOnce },
        ]);

    [Fact]
    public async Task ToggleRemoteControl_TurnsOnWithSessionUrl_ThenOff()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), @"D:\dev\my-solution"));
        await vm.InitializeAsync();
        vm.InputText = "hi";
        await vm.SendAsync(); // ensures a session exists
        Assert.False(vm.IsRemoteControlEnabled);
        Assert.Empty(connection.RemoteControlCalls);

        await vm.ToggleRemoteControlCommand.ExecuteAsync(null);

        var call = Assert.Single(connection.RemoteControlCalls);
        Assert.True(call.Enabled);
        Assert.Equal("Visual Studio · my-solution", call.Name);
        Assert.True(vm.IsRemoteControlEnabled);
        Assert.Equal("https://claude.ai/code/session/test", vm.RemoteControlUrl);

        await vm.ToggleRemoteControlCommand.ExecuteAsync(null);

        Assert.False(connection.RemoteControlCalls[^1].Enabled);
        Assert.False(vm.IsRemoteControlEnabled);
        Assert.Null(vm.RemoteControlUrl);
    }

    [Fact]
    public async Task RemoteControlAtStartup_TurnsRemoteControlOn_ForEveryNewSession()
    {
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()) { RemoteControlAtStartup = true };
        using var vm = new ChatViewModel(services);
        await vm.InitializeAsync();
        vm.InputText = "hi";
        await vm.SendAsync();
        await WaitUntilAsync(() => connection.RemoteControlCalls.Count >= 1 && vm.IsRemoteControlEnabled);

        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult("session-2", []));
        await vm.NewSessionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => connection.RemoteControlCalls.Count >= 2);
        Assert.Equal("session-2", connection.RemoteControlCalls[^1].SessionId);
        Assert.True(connection.RemoteControlCalls[^1].Enabled);
    }

    [Fact]
    public async Task SendAsync_WithImageAttachment_KeepsImageOnUserMessage_InsteadOfTextPlaceholder()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.AddImageAttachment("shot.png", "image/png", "AQID");
        vm.InputText = "what is this?";

        await vm.SendAsync();

        var user = Assert.Single(vm.Messages, m => m.Role == ChatRole.User);
        Assert.Equal("what is this?", user.Text);
        var image = Assert.Single(user.Images);
        Assert.Equal("shot.png", image.Name);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("AQID", image.Base64Data);
    }

    [Fact]
    public async Task AttentionRequested_FiresForFinishedTurn_PermissionPlanReviewAndElicitation()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        var raised = new List<ChatAttentionEventArgs>();
        vm.AttentionRequested += (_, e) => raised.Add(e);

        vm.InputText = "do something";
        await vm.SendAsync();
        connection.RaisePermissionRequested(
            new ToolCallUpdate { ToolCallId = "t1", Title = "Edit Program.cs", Status = ToolCallStatus.Pending },
            [new PermissionOption { OptionId = "allow-once", Label = "Yes", Outcome = PermissionOutcome.AllowOnce }]);
        vm.PendingPermission!.ChooseCommand.Execute(vm.PendingPermission.Options[0]);
        // \r alone is a line break to WPF and to a Windows toast, so the one-line notification
        // text must stop at it just as it stops at \n.
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("All done.\rDetails follow.\nMore."));
        connection.RaiseSessionUpdate(new SessionUpdate.TurnEnded("end_turn"));
        var (planCall, planOptions) = PlanApprovalRequest("# Plan");
        connection.RaisePermissionRequested(planCall, planOptions);
        // A form blocks the turn exactly as a permission request does, so it needs the same nudge.
        connection.RaiseElicitationRequested("Which environment?", [new ElicitationField("q0", null, null, ElicitationFieldKind.Text, [])]);

        Assert.Collection(raised,
            e => { Assert.Equal(ChatAttentionKind.PermissionNeeded, e.Kind); Assert.Equal("Edit Program.cs", e.Message); },
            e => { Assert.Equal(ChatAttentionKind.TurnCompleted, e.Kind); Assert.Equal("All done.", e.Message); },
            e => Assert.Equal(ChatAttentionKind.PlanReview, e.Kind),
            e => { Assert.Equal(ChatAttentionKind.PermissionNeeded, e.Kind); Assert.Equal("Which environment?", e.Message); });
    }

    [Fact]
    public async Task UsageUpdate_DrivesTurnTokens_AndContextUsagePercent()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        Assert.Null(vm.ContextUsagePercent);

        vm.InputText = "hello";
        var prompt = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => prompt.Task;
        var sending = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.UsageUpdate(50_000, 200_000, null, null));
        Assert.Equal(25, vm.ContextUsagePercent);
        Assert.Equal("Context: 50k / 200k (25%)", vm.ContextUsageLabel);
        Assert.Equal(50_000, vm.TurnTokens);

        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("hi"));
        connection.RaiseSessionUpdate(new SessionUpdate.TurnEnded("end_turn"));
        prompt.SetResult(true);
        await sending;
        Assert.Equal(50_000, Assert.Single(vm.Messages, m => m.Role == ChatRole.Assistant).TokensUsed);
    }

    [Fact]
    public async Task PlanApproval_ExposesPendingPlan_AndProceedAnswersWithAllowOption()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "plan the feature";
        await vm.SendAsync();
        var (call, options) = PlanApprovalRequest("# Plan\n\n1. Add tests\n2. Implement");

        var request = connection.RaisePermissionRequested(call, options);

        Assert.NotNull(vm.PendingPlan);
        Assert.Equal("# Plan\n\n1. Add tests\n2. Implement", vm.PendingPlan!.Markdown);
        Assert.False(vm.PendingPlan.IsResolved);

        vm.PendingPlan.ProceedCommand.Execute(null);

        Assert.Equal("allow-once", await request.Response.Task);
        Assert.True(vm.PendingPlan.IsResolved);
        Assert.Null(vm.PendingPermission);
    }

    [Fact]
    public async Task PlanReview_RejectsThePlan_AndSendsCommentsAsTheNextPrompt()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "plan the feature";
        await vm.SendAsync();
        var promptsBefore = connection.Prompts.Count;
        var (call, options) = PlanApprovalRequest("# Plan");
        var request = connection.RaisePermissionRequested(call, options);

        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step and cover the error path with tests.");

        Assert.Equal("reject-once", await request.Response.Task);
        connection.RaiseSessionUpdate(new SessionUpdate.TurnEnded("end_turn"));
        await WaitUntilAsync(() => connection.Prompts.Count > promptsBefore);
        var followUp = Assert.IsType<ContentBlock.Text>(connection.Prompts[^1][0]);
        Assert.Contains("Add a rollback step", followUp.Value);
        Assert.True(vm.PendingPlan.IsResolved);
    }

    // The composer stays live while the rejected plan's turn finishes, so the user can be mid-
    // sentence when the review goes out. The review is its own prompt; the draft is not its input.
    [Fact]
    public async Task PlanReview_SendsItsOwnPrompt_WithoutConsumingTheUsersDraft()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "plan the feature";
        await vm.SendAsync();
        var promptsBefore = connection.Prompts.Count;
        var (call, options) = PlanApprovalRequest("# Plan");
        connection.RaisePermissionRequested(call, options);
        vm.InputText = "meanwhile, what about the CI job?";

        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");

        await WaitUntilAsync(() => connection.Prompts.Count > promptsBefore);
        var followUp = Assert.IsType<ContentBlock.Text>(connection.Prompts[^1][0]);
        Assert.Contains("Add a rollback step", followUp.Value);
        Assert.DoesNotContain("CI job", followUp.Value, StringComparison.Ordinal);
        await WaitUntilAsync(() => vm.InputText.Length > 0);
        Assert.Equal("meanwhile, what about the CI job?", vm.InputText);
    }

    [Fact]
    public async Task AttachActiveDocumentCommand_IsDisabled_UntilHostReportsAnActiveDocument()
    {
        var services = new StubChatSessionServices(new SingleConnectionFactory(new RecordingAcpAgentConnection()), new AlwaysSignedInAuthService());
        services.SetHasActiveDocument(false);
        using var vm = new ChatViewModel(services);
        await vm.InitializeAsync();

        Assert.False(vm.AttachActiveDocumentCommand.CanExecute(null));
        Assert.False(vm.HasActiveDocument);

        var canExecuteChanges = 0;
        vm.AttachActiveDocumentCommand.CanExecuteChanged += (_, __) => canExecuteChanges++;
        services.SetHasActiveDocument(true);

        Assert.True(vm.AttachActiveDocumentCommand.CanExecute(null));
        Assert.True(vm.HasActiveDocument);
        Assert.True(canExecuteChanges > 0);
    }

    [Fact]
    public async Task SessionTitle_FollowsFirstPrompt_NewSession_AndOpenedSessionTitle()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), "/workspace"));
        await vm.InitializeAsync();
        Assert.Equal("Untitled", vm.SessionTitle);

        vm.InputText = "Fix the login bug\nwith more details";
        await vm.SendAsync();
        Assert.Equal("Fix the login bug", vm.SessionTitle);

        vm.InputText = "second prompt";
        await vm.SendAsync();
        Assert.Equal("Fix the login bug", vm.SessionTitle);

        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult("session-2", []));
        await vm.NewSessionCommand.ExecuteAsync(null);
        Assert.Equal("Untitled", vm.SessionTitle);

        connection.LoadSessionHandler = (sessionId, cwd, mcpServers, _) =>
        {
            connection.RaiseSessionUpdate(new SessionUpdate.UserMessageChunk("What does this do?"), sessionId);
            return Task.FromResult(new NewSessionResult(sessionId, []));
        };
        await vm.OpenSessionCommand.ExecuteAsync(new SessionSummary("s-old", "/workspace", "Older chat", null));
        Assert.Equal("Older chat", vm.SessionTitle);

        await vm.OpenSessionCommand.ExecuteAsync(new SessionSummary("s-old-2", "/workspace", null, null));
        Assert.Equal("What does this do?", vm.SessionTitle);
    }

    [Fact]
    public async Task Usage_IsRefetched_WhenUsagePanelOpens_AndWhenTurnEnds()
    {
        var usageService = new CountingUsageService();
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), usageService: usageService));
        await vm.InitializeAsync();
        await usageService.WaitForCallsAsync(1);
        Assert.Equal(10, vm.Usage!.Limits[0].Percent);

        usageService.Percent = 42;
        vm.IsUsagePanelOpen = true;
        await usageService.WaitForCallsAsync(2);
        await WaitUntilAsync(() => vm.Usage!.Limits[0].Percent == 42);

        usageService.Percent = 77;
        vm.InputText = "hello";
        await vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.TurnEnded("end_turn"));
        await usageService.WaitForCallsAsync(3);
        await WaitUntilAsync(() => vm.Usage!.Limits[0].Percent == 77);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class CountingUsageService : IUsageService
    {
        private int _calls;
        public int Percent { get; set; } = 10;
        public DateTimeOffset? ResetsAt { get; set; }
        /// <summary>Thrown synchronously from the next fetches while set - the harsher of the two
        /// ways an <see cref="IUsageService"/> can fail on a caller.</summary>
        public Exception? Failure { get; set; }

        public Task<UsageSnapshot?> GetUsageAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Failure is not null) throw Failure;
            return Task.FromResult<UsageSnapshot?>(new UsageSnapshot
            {
                Limits = [new UsageLimit { Kind = "session", Group = "session", Percent = Percent, ResetsAt = ResetsAt }],
                FetchedAt = DateTimeOffset.UtcNow,
            });
        }

        public async Task WaitForCallsAsync(int expected)
        {
            for (var i = 0; i < 200 && Volatile.Read(ref _calls) < expected; i++) await Task.Delay(10);
            Assert.True(Volatile.Read(ref _calls) >= expected, $"Expected at least {expected} usage fetches, saw {_calls}.");
        }
    }

    [Fact]
    public async Task HistoryFilter_NarrowsSessionHistory_ByTitleOrSessionId_CaseInsensitive()
    {
        var connection = new RecordingAcpAgentConnection
        {
            ListSessionsHandler = (cwd, _) => Task.FromResult<IReadOnlyList<SessionSummary>>(
            [
                new SessionSummary("s1", cwd!, "Fix the login bug", null),
                new SessionSummary("s2", cwd!, "Refactor payments", null),
                new SessionSummary("untitled-session-3", cwd!, null, null),
            ]),
        };
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        await vm.ShowHistoryCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.SessionHistory.Count);

        vm.HistoryFilter = "LOGIN";
        Assert.Equal(["Fix the login bug"], vm.SessionHistory.Select(s => s.Title));

        vm.HistoryFilter = "untitled-session-3";
        Assert.Equal(["untitled-session-3"], vm.SessionHistory.Select(s => s.SessionId));

        vm.HistoryFilter = "";
        Assert.Equal(3, vm.SessionHistory.Count);

        vm.HistoryFilter = "does not match anything";
        Assert.Empty(vm.SessionHistory);
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

    // The warning state machine is three interacting variables (threshold, dismissal, and the
    // percent at which it was dismissed) and drives a banner in the composer area; a "<" vs "<="
    // slip makes it either impossible to silence or impossible to re-raise.
    [Fact]
    public async Task UsageWarning_AppearsAtTheThreshold_StaysDismissedUntilUsageClimbs_AndClearsWhenItDrops()
    {
        var usageService = new CountingUsageService { Percent = 70 };
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), usageService: usageService));
        await vm.InitializeAsync();
        await usageService.WaitForCallsAsync(1);
        await WaitUntilAsync(() => vm.Usage is not null);
        Assert.Null(vm.UsageWarning);

        await PollUsageAsync(vm, usageService, 80);
        Assert.NotNull(vm.UsageWarning);
        Assert.Contains("80%", vm.UsageWarning!.Message, StringComparison.Ordinal);

        vm.DismissUsageWarning();
        Assert.Null(vm.UsageWarning);

        // Same percent a poll later: a dismissal must not be undone by the very next fetch.
        await PollUsageAsync(vm, usageService, 80);
        Assert.Null(vm.UsageWarning);

        await PollUsageAsync(vm, usageService, 85);
        Assert.NotNull(vm.UsageWarning);

        // Back under the threshold clears the banner and forgets the dismissal.
        await PollUsageAsync(vm, usageService, 10);
        Assert.Null(vm.UsageWarning);
        await PollUsageAsync(vm, usageService, 80);
        Assert.NotNull(vm.UsageWarning);
    }

    private static async Task PollUsageAsync(ChatViewModel vm, CountingUsageService usageService, int percent)
    {
        usageService.Percent = percent;
        vm.IsUsagePanelOpen = false;
        vm.IsUsagePanelOpen = true;
        await WaitUntilAsync(() => vm.Usage!.Limits[0].Percent == percent);
    }

    [Fact]
    public async Task NewSession_ResetsTheContextUsageOfThePreviousSession()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "hello";
        var prompt = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => prompt.Task;
        var sending = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.UsageUpdate(150_000, 200_000, null, null));
        Assert.Equal(75, vm.ContextUsagePercent);
        prompt.SetResult(true);
        await sending;

        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult("session-2", []));
        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Null(vm.ContextUsagePercent);
        Assert.Equal(0, vm.SessionUsedTokens);
        Assert.Null(vm.ContextWindowSize);
        Assert.Null(vm.TurnTokens);
    }

    // SessionSummary.Title is agent-reported and is bound straight into the single-row panel header
    // plus its tooltip, so it gets the same first-line/80-char normalization as a locally derived
    // title - the trusted source was capped and the untrusted one was not.
    [Fact]
    public async Task OpenSession_NormalizesTheAgentSuppliedTitle()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), "/workspace"));
        await vm.InitializeAsync();

        await vm.OpenSessionCommand.ExecuteAsync(new SessionSummary("s1", "/workspace", "  \n  first line  \nsecond line", null));
        Assert.Equal("first line", vm.SessionTitle);

        await vm.OpenSessionCommand.ExecuteAsync(new SessionSummary("s2", "/workspace", new string('x', 500), null));
        Assert.Equal(80, vm.SessionTitle.Length);
    }

    // A superseded form can still be on screen in a host surface; submitting it must not wipe the
    // form the user is actually looking at (whose slot would then never be answered).
    [Fact]
    public async Task SupersededElicitation_SubmittedLate_DoesNotClearTheCurrentForm()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "ask me twice";
        await vm.SendAsync();

        connection.RaiseElicitationRequested("First question", [new ElicitationField("q0", null, null, ElicitationFieldKind.Text, [])]);
        var superseded = vm.PendingElicitation!;
        var secondArgs = connection.RaiseElicitationRequested("Second question", [new ElicitationField("q1", null, null, ElicitationFieldKind.Text, [])]);
        var current = vm.PendingElicitation!;
        Assert.NotSame(superseded, current);

        superseded.SubmitCommand.Execute(null);

        Assert.Same(current, vm.PendingElicitation);
        current.SubmitCommand.Execute(null);
        Assert.Equal(ElicitationAction.Accept, (await secondArgs.Response.Task.WaitAsync(TimeSpan.FromSeconds(5))).Action);
    }

    // An abandoned plan keeps a live Proceed/Review in any still-open plan window; clicking Review
    // there would overwrite the new session's composer draft and send the comments to it.
    [Fact]
    public async Task NewSession_ResolvesAnAbandonedPlan_SoItsCommandsGoDead()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "plan the feature";
        await vm.SendAsync();
        var (call, options) = PlanApprovalRequest("# Plan");
        connection.RaisePermissionRequested(call, options);
        var abandoned = vm.PendingPlan!;
        Assert.False(abandoned.IsResolved);

        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult("session-2", []));
        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.True(abandoned.IsResolved);
        Assert.False(abandoned.ProceedCommand.CanExecute(null));
        Assert.False(abandoned.ReviewCommand.CanExecute("late comments"));
    }

    // A switch_mode request has two surfaces, the permission card and the plan document. Answering
    // from the card must settle the document too: a still-live Proceed there would answer a request
    // that is already resolved.
    [Fact]
    public async Task PlanApproval_AnsweredFromThePermissionCard_ResolvesThePlanDocument()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "plan the feature";
        await vm.SendAsync();
        var (call, options) = PlanApprovalRequest("# Plan");
        var request = connection.RaisePermissionRequested(call, options);
        var plan = vm.PendingPlan!;

        vm.PendingPermission!.ChooseCommand.Execute(vm.PendingPermission.Options[1]); // "No, keep planning"

        Assert.Equal("reject-once", await request.Response.Task);
        Assert.Null(vm.PendingPermission);
        Assert.True(plan.IsResolved);
        Assert.False(plan.ProceedCommand.CanExecute(null));
        Assert.False(plan.ReviewCommand.CanExecute("late comments"));
    }

    // Both surfaces can outlive their request. Answering a stale one must not disturb the request
    // that replaced it: clearing the newer card would leave its slot unanswered until the
    // connection is torn down, and the plan document must go dead rather than stay clickable.
    [Fact]
    public async Task StalePlanSurfaces_AnsweredAfterANewerPermissionArrived_LeaveTheNewerRequestIntact()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "plan the feature";
        await vm.SendAsync();
        var (planCall, planOptions) = PlanApprovalRequest("# Plan");
        var planRequest = connection.RaisePermissionRequested(planCall, planOptions);
        var stalePlan = vm.PendingPlan!;
        var staleCard = vm.PendingPermission!;
        var newer = connection.RaisePermissionRequested(
            new ToolCallUpdate { ToolCallId = "tc-2", Title = "Edit b.cs", Status = ToolCallStatus.Pending },
            [new PermissionOption { OptionId = "allow-b", Label = "Allow", Outcome = PermissionOutcome.AllowOnce }]);
        await Assert.ThrowsAsync<OperationCanceledException>(() => planRequest.Response.Task);
        var current = vm.PendingPermission!;

        stalePlan.ProceedCommand.Execute(null);
        staleCard.ChooseCommand.Execute(staleCard.Options[0]);

        Assert.Same(current, vm.PendingPermission);
        Assert.False(newer.Response.Task.IsCompleted);
        Assert.True(stalePlan.IsResolved);
        current.ChooseCommand.Execute(current.Options[0]);
        Assert.Equal("allow-b", await newer.Response.Task);
        Assert.Null(vm.PendingPermission);
    }

    // The launcher can refuse a toggle (no claude.ai session, network). The UI must then keep the
    // last acknowledged state, say why, and hand the toggle back rather than leave it stuck busy.
    [Fact]
    public async Task RemoteControl_WhenTheToggleFails_KeepsTheAcknowledgedStateReportsItAndStaysUsable()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "hi";
        await vm.SendAsync();
        await vm.ToggleRemoteControlCommand.ExecuteAsync(null);
        Assert.True(vm.IsRemoteControlEnabled);
        connection.RemoteControlHandler = _ => Task.FromException<RemoteControlState>(new InvalidOperationException("the launcher refused"));

        await vm.ToggleRemoteControlCommand.ExecuteAsync(null);

        Assert.True(vm.IsRemoteControlEnabled);
        Assert.Equal("https://claude.ai/code/session/test", vm.RemoteControlUrl);
        Assert.Equal("Remote Control: the launcher refused", vm.StatusMessage);
        Assert.False(vm.IsRemoteControlBusy);
        Assert.True(vm.ToggleRemoteControlCommand.CanExecute(null));

        connection.RemoteControlHandler = null;
        await vm.ToggleRemoteControlCommand.ExecuteAsync(null);
        Assert.False(vm.IsRemoteControlEnabled);
        Assert.Null(vm.RemoteControlUrl);
        Assert.Null(vm.StatusMessage);
    }

    // Usage is best-effort presentation: a fetch that throws - on open, after a turn - must neither
    // surface to the caller nor disturb the last good snapshot, and the next fetch must still run.
    [Fact]
    public async Task UsageService_ThatThrows_LeavesTheLastSnapshotAndRecoversOnTheNextFetch()
    {
        var usageService = new CountingUsageService();
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), usageService: usageService));
        await vm.InitializeAsync();
        await usageService.WaitForCallsAsync(1);
        await WaitUntilAsync(() => vm.Usage is not null);
        usageService.Failure = new InvalidOperationException("node is missing");

        vm.IsUsagePanelOpen = true;
        await usageService.WaitForCallsAsync(2);
        vm.InputText = "hello";
        await vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.TurnEnded("end_turn"));
        await usageService.WaitForCallsAsync(3);

        Assert.Equal(10, vm.Usage!.Limits[0].Percent);
        Assert.Null(vm.StatusMessage);

        usageService.Failure = null;
        usageService.Percent = 42;
        vm.IsUsagePanelOpen = false;
        vm.IsUsagePanelOpen = true;
        await WaitUntilAsync(() => vm.Usage!.Limits[0].Percent == 42);
    }

    // The reset label is the culture's own short time: devenv hosts this assembly on net472, where
    // NLS gives de-DE, fr-FR, it-IT an empty AM/PM designator, and "h:mm tt" would show a 15:00
    // reset as "3:00 " - twelve hours off for those users.
    [Theory]
    [InlineData(2)]
    [InlineData(72)]
    public async Task UsageResetLabel_UsesTheCulturesShortTimePattern_UnderACultureWithoutAMeridiem(int hoursAhead)
    {
        var resetsAt = new DateTimeOffset(DateTimeOffset.UtcNow.AddHours(hoursAhead).AddMinutes(30).Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
        var usageService = new CountingUsageService { ResetsAt = resetsAt };
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), usageService: usageService));
        await vm.InitializeAsync();
        await WaitUntilAsync(() => vm.Usage is not null);
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.AMDesignator = string.Empty;
        culture.DateTimeFormat.PMDesignator = string.Empty;
        culture.DateTimeFormat.ShortTimePattern = "HH:mm";
        var local = resetsAt.ToLocalTime();
        var expected = hoursAhead < 20
            ? "Resets " + local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : "Resets " + local.ToString("dddd", culture) + " " + local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal(expected, vm.SessionUsage!.ResetText);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // The agent keeps every session it created, so a session left published to claude.ai/code
    // stays there - enabled, invisible, and unreachable from a toggle that only addresses the
    // current session. Leaving a session turns its Remote Control off first, on both switch paths.
    [Fact]
    public async Task LeavingASession_WithRemoteControlOn_TurnsItOffBeforeAdoptingTheNextOne()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), "/workspace"));
        await vm.InitializeAsync();
        vm.InputText = "hi";
        await vm.SendAsync();
        await vm.ToggleRemoteControlCommand.ExecuteAsync(null);
        Assert.True(vm.IsRemoteControlEnabled);
        var disabledBeforeNewSession = false;
        connection.NewSessionHandler = _ =>
        {
            disabledBeforeNewSession = connection.RemoteControlCalls.Any(c => c.SessionId == RecordingAcpAgentConnection.SessionId && !c.Enabled);
            return Task.FromResult(new NewSessionResult("session-2", []));
        };

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.True(disabledBeforeNewSession);
        Assert.False(vm.IsRemoteControlEnabled);
        Assert.Null(vm.RemoteControlUrl);

        await vm.ToggleRemoteControlCommand.ExecuteAsync(null);
        Assert.True(vm.IsRemoteControlEnabled);
        var disabledBeforeLoad = false;
        connection.LoadSessionHandler = (_, _, _, _) =>
        {
            disabledBeforeLoad = connection.RemoteControlCalls.Any(c => c.SessionId == "session-2" && !c.Enabled);
            return Task.FromResult(new NewSessionResult("session-old", []));
        };

        await vm.OpenSessionCommand.ExecuteAsync(new SessionSummary("session-old", "/workspace", "Older chat", null));

        Assert.True(disabledBeforeLoad);
        Assert.False(vm.IsRemoteControlEnabled);
    }

    // A toggle still in flight when the session changes must not cost the incoming session its
    // startup enable: the stale result is discarded, and the enable is issued for whichever
    // session is current once the toggle returns.
    [Fact]
    public async Task RemoteControlAtStartup_WithAToggleInFlightDuringNewChat_StillEnablesTheNewSession()
    {
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()) { RemoteControlAtStartup = true };
        using var vm = new ChatViewModel(services);
        await vm.InitializeAsync();
        await WaitUntilAsync(() => vm.IsRemoteControlEnabled);
        var inFlight = new TaskCompletionSource<RemoteControlState>();
        connection.RemoteControlHandler = _ => inFlight.Task;
        var toggling = vm.ToggleRemoteControlCommand.ExecuteAsync(null);
        connection.RemoteControlHandler = null; // only that one call stays in flight
        Assert.True(vm.IsRemoteControlBusy);
        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult("session-2", []));
        await vm.NewSessionCommand.ExecuteAsync(null);
        Assert.False(vm.IsRemoteControlEnabled);

        inFlight.SetException(new InvalidOperationException("late"));
        await toggling;

        await WaitUntilAsync(() => vm.IsRemoteControlEnabled);
        Assert.Contains(connection.RemoteControlCalls, c => c.SessionId == "session-2" && c.Enabled);
    }

    // The other half of the same orphan: an enable that was in flight for the session being left
    // lands after the switch. Its result is stale for the UI, but on the agent that session is now
    // published - so it is turned back off.
    [Fact]
    public async Task RemoteControlEnable_LandingAfterTheSessionWasLeft_TurnsThatSessionOffAgain()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();
        vm.InputText = "hi";
        await vm.SendAsync();
        var inFlight = new TaskCompletionSource<RemoteControlState>();
        connection.RemoteControlHandler = _ => inFlight.Task;
        var enabling = vm.ToggleRemoteControlCommand.ExecuteAsync(null);
        connection.RemoteControlHandler = null; // only that one call stays in flight
        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult("session-2", []));
        await vm.NewSessionCommand.ExecuteAsync(null);

        inFlight.SetResult(RemoteControl(enabled: true));
        await enabling;

        await WaitUntilAsync(() => connection.RemoteControlCalls.Any(c => c.SessionId == RecordingAcpAgentConnection.SessionId && !c.Enabled));
        Assert.False(vm.IsRemoteControlEnabled);
        Assert.Null(vm.RemoteControlUrl);
        Assert.DoesNotContain(connection.RemoteControlCalls, c => c.SessionId == "session-2");
    }

    private static RemoteControlState RemoteControl(bool enabled) =>
        new(enabled, enabled ? "https://claude.ai/code/session/test" : null);

    // A Bash/PowerShell title is the command itself, and this client (no terminal capability)
    // gets nothing else to show, so the whole command - every line - has to reach the card the
    // user answers from. Height is bounded by the card's ScrollViewer, not by cutting text.
    [Fact]
    public async Task PermissionRequested_ForAMultiLineShellCommand_ShowsEveryLineOfTheCommand()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "commit it";
        await vm.SendAsync();

        const string command = "git add -A\ngit commit -m \"wip\" && rm -rf build/";
        var call = new ToolCallUpdate { ToolCallId = "tc-bash", Title = command, Status = ToolCallStatus.Pending };
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(call));
        connection.RaisePermissionRequested(call,
            [new PermissionOption { OptionId = "allow-once", Label = "Allow", Outcome = PermissionOutcome.AllowOnce }]);

        Assert.Equal(command, vm.PendingPermission!.Title);
        var card = Assert.Single(vm.Messages.SelectMany(message => message.ToolCalls), tool => tool.ToolCallId == "tc-bash");
        Assert.Equal(command, card.Title);
    }
}
