using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed partial class ChatSessionStateTests
{
    private static StubChatSessionServices ServicesFor(RecordingAcpAgentConnection connection) =>
        new(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService());

    private static bool WasLogged(StubChatSessionServices services, string message) =>
        services.LoggedErrors.Any(logged => logged.Exception.Message == message);

    [Fact]
    public async Task AttachDocument_ObserverThrowsAsTheCaptureStarts_IsReported_AndDoesNotLockTheComposer()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        services.CaptureHandler = _ => Task.FromResult<EditorDocumentSnapshot?>(new EditorDocumentSnapshot(@"C:\Workspace\a.cs", "text"));
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "draft";
        bool armed = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (!armed || e.PropertyName != nameof(ChatViewModel.CanConfigure) || vm.CanConfigure) return;
            armed = false;
            throw new InvalidOperationException("observer failed");
        };

        var failure = await Record.ExceptionAsync(() => vm.AttachActiveDocumentCommand.ExecuteAsync(null));

        Assert.Null(failure);
        Assert.True(vm.CanConfigure);
        Assert.True(vm.SendCommand.CanExecute(null));
        Assert.True(vm.AttachActiveDocumentCommand.CanExecute(null));
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task AttachDocument_ObserverThrowsAsTheCaptureEnds_StillSendsTheReviewItHeldBack()
    {
        var captured = new TaskCompletionSource<EditorDocumentSnapshot?>();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        services.CaptureHandler = _ => captured.Task;
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        var capture = vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");
        Assert.Empty(connection.Prompts);
        bool armed = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (!armed || e.PropertyName != nameof(ChatViewModel.CanConfigure) || !vm.CanConfigure) return;
            armed = false;
            throw new InvalidOperationException("observer failed");
        };

        captured.SetResult(null);
        var failure = await Record.ExceptionAsync(() => capture);

        Assert.Null(failure);
        await WaitUntilAsync(() => connection.Prompts.Count == 1);
        Assert.StartsWith("Review comments on the plan:", Text(connection.Prompts[0]), StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task ConfigChange_ObserverThrowsAsTheChangeStarts_IsReported_AndDoesNotLockTheComposer()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "draft";
        bool armed = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (!armed || e.PropertyName != nameof(ChatViewModel.IsConfigBusy) || !vm.IsConfigBusy) return;
            armed = false;
            throw new InvalidOperationException("observer failed");
        };

        var failure = await Record.ExceptionAsync(() => vm.SelectModelAsync(vm.AvailableModels[1]));

        Assert.Null(failure);
        Assert.False(vm.IsConfigBusy);
        Assert.True(vm.CanConfigure);
        Assert.True(vm.SendCommand.CanExecute(null));
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task ConfigChange_ObserverThrowsAsTheChangeEnds_StillSendsTheMessageQueuedBehindIt()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var config = new TaskCompletionSource<IReadOnlyList<SessionConfigOption>>();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options(), PromptHandler = _ => firstTurn.Task };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "queued behind the first";
        await vm.SendAsync();
        connection.ConfigHandler = (_, _, _) => config.Task;
        var configChange = vm.SelectModelAsync(vm.AvailableModels[1]);
        firstTurn.SetResult(true);
        await firstSend;
        Assert.Single(connection.Prompts);
        bool armed = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (!armed || e.PropertyName != nameof(ChatViewModel.IsConfigBusy) || vm.IsConfigBusy) return;
            armed = false;
            throw new InvalidOperationException("observer failed");
        };

        config.SetResult(Options("opus"));
        var failure = await Record.ExceptionAsync(() => configChange);

        Assert.Null(failure);
        await WaitUntilAsync(() => connection.Prompts.Count == 2);
        Assert.Equal("queued behind the first", Text(connection.Prompts[1]));
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task ConfigChange_ObserverThrowsAsAnUnchangedSelectionIsRepublished_IsReported()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.SelectedModel)) throw new InvalidOperationException("observer failed");
        };

        var failure = await Record.ExceptionAsync(() => vm.SelectModelAsync(vm.SelectedModel));

        Assert.Null(failure);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task AutoEffort_ObserverThrowsAsAutoIsSelected_IsReported()
    {
        var (connection, _) = AutoConnection("medium");
        var services = ServicesFor(connection);
        services.EffortClassifier = new FakeEffortClassifier();
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.SelectedEffort)) throw new InvalidOperationException("observer failed");
        };

        var failure = await Record.ExceptionAsync(() => vm.SelectEffortAsync(Auto(vm)));

        Assert.Null(failure);
        Assert.Equal("Auto", vm.ActiveEffortName);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task ExplicitEffort_ObserverThrowsAsAutoIsLeft_StillSendsTheFollowUpThatWaited()
    {
        var (connection, log) = AutoConnection("medium");
        var firstTurn = new TaskCompletionSource();
        connection.PromptHandler = content =>
        {
            var text = ((ContentBlock.Text)content[0]).Value;
            log.Add("prompt:" + text);
            return text == "first" ? firstTurn.Task : Task.CompletedTask;
        };
        var inner = connection.ConfigHandler!;
        var holdRequests = false;
        var release = new TaskCompletionSource();
        connection.ConfigHandler = async (session, value, token) =>
        {
            if (holdRequests) await release.Task;
            return await inner(session, value, token);
        };
        var services = ServicesFor(connection);
        services.EffortClassifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        var running = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => log.Contains("prompt:first"));
        await SendTextAsync(vm, "second");
        holdRequests = true;
        var picking = vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "xhigh"));
        await WaitUntilAsync(() => connection.ConfigChanges.Count == 2);
        firstTurn.SetResult();
        await WithinAsync(running);
        bool armed = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (!armed || e.PropertyName != nameof(ChatViewModel.SelectedEffort) || vm.SelectedEffort?.Value != "xhigh") return;
            armed = false;
            throw new InvalidOperationException("observer failed");
        };

        release.SetResult();
        var failure = await Record.ExceptionAsync(() => picking);

        Assert.Null(failure);
        await WaitUntilAsync(() => log.Contains("prompt:second") && !vm.IsBusy);
        Assert.Equal("xhigh", vm.SelectedEffort!.Value);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task Login_ObserverThrowsAsTheCommandStarts_IsReported_AndDoesNotStayRunning()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        bool armed = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (!armed || e.PropertyName != nameof(ChatViewModel.IsAuthCommandRunning) || !vm.IsAuthCommandRunning) return;
            armed = false;
            throw new InvalidOperationException("observer failed");
        };
        vm.InputText = "/login";

        var failure = await Record.ExceptionAsync(() => vm.SendAsync());

        Assert.Null(failure);
        Assert.False(vm.IsAuthCommandRunning);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
        vm.InputText = "/login";
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task PlanReview_ObserverThrowsAsThePlanIsResolved_StillAnswersTheRequest_AndSendsTheReview()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        var (call, options) = PlanApprovalRequest();
        var request = connection.RaisePermissionRequested(call, options);
        var plan = vm.PendingPlan!;
        plan.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlanReviewViewModel.Status) && plan.Status == "Sent back for revision")
                throw new InvalidOperationException("observer failed");
        };

        var failure = Record.Exception(() => plan.ReviewCommand.Execute("Add a rollback step."));

        Assert.Null(failure);
        await WithinAsync(request.Response.Task);
        Assert.Equal("reject-once", await request.Response.Task);
        await WaitUntilAsync(() => connection.Prompts.Count == 1);
        Assert.StartsWith("Review comments on the plan:", Text(connection.Prompts[0]), StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task PlanReview_ObserverThrowsAsThePermissionCardCloses_StillSendsTheReview()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        var (call, options) = PlanApprovalRequest();
        var request = connection.RaisePermissionRequested(call, options);
        var plan = vm.PendingPlan!;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.PendingPermission) && vm.PendingPermission is null)
                throw new InvalidOperationException("observer failed");
        };

        var failure = Record.Exception(() => plan.ReviewCommand.Execute("Add a rollback step."));

        Assert.Null(failure);
        Assert.Equal("reject-once", await request.Response.Task);
        Assert.True(plan.IsResolved);
        await WaitUntilAsync(() => connection.Prompts.Count == 1);
        Assert.StartsWith("Review comments on the plan:", Text(connection.Prompts[0]), StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task QueuedMessage_ObserverThrowsAsItIsMarkedSent_StillGoesOut()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection
        {
            ConfigOptions = Options(),
            PromptHandler = content => Text(content) == "first" ? firstTurn.Task : Task.CompletedTask,
        };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        var queued = Assert.Single(vm.Messages, message => message.Text == "second");
        queued.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMessageViewModel.IsPending)) throw new InvalidOperationException("observer failed");
        };

        firstTurn.SetResult(true);
        await firstSend;

        await WaitUntilAsync(() => connection.Prompts.Count == 2 && !vm.IsBusy);
        Assert.Equal("second", Text(connection.Prompts[1]));
        Assert.False(queued.IsPending);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task CancelFailure_IsShownAndLogged()
    {
        var turn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options(), PromptHandler = _ => turn.Task };
        connection.CancelHandler = () => Task.FromException(new InvalidOperationException("cancel rejected"));
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "long turn";
        var sending = vm.SendAsync();

        await vm.CancelAsync();

        Assert.Contains("cancel rejected", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "cancel rejected"));
        turn.SetResult(true);
        await sending;
    }

    [Fact]
    public async Task ConfigChangeFailure_IsShownAndLogged()
    {
        var connection = new RecordingAcpAgentConnection
        {
            ConfigOptions = Options(),
            ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("Setting rejected")),
        };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;

        await vm.SelectModelAsync(vm.AvailableModels[1]);

        Assert.Contains("Setting rejected", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "Setting rejected"));
    }

    [Fact]
    public async Task CaptureFailure_IsShownAndLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        services.CaptureHandler = _ => Task.FromException<EditorDocumentSnapshot?>(new InvalidOperationException("Editor unavailable"));
        using var vm = new ChatViewModel(services);
        await vm.Initialization;

        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);

        Assert.Contains("Editor unavailable", vm.AttachmentError, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "Editor unavailable"));
    }

    [Fact]
    public async Task PrepareFailure_IsShownAndLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var factory = new SingleConnectionFactory(connection)
        {
            ConnectHandler = _ => Task.FromException<IAcpAgentConnection>(new InvalidOperationException("Unavailable")),
        };
        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService());
        using var vm = new ChatViewModel(services);

        await vm.Initialization;

        Assert.Contains("Unavailable", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "Unavailable"));
    }

    [Fact]
    public async Task LoginFailure_IsShownAndLogged()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        auth.LoginHandler = (_, _) => Task.FromException<AuthCommandOutcome>(new InvalidOperationException("console unavailable"));
        vm.InputText = "/login";

        await vm.SendAsync();

        Assert.Contains("console unavailable", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "console unavailable"));
    }

    private static void ThrowOnceWhen(ChatViewModel vm, string property, Func<bool> condition)
    {
        bool armed = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (!armed || e.PropertyName != property || !condition()) return;
            armed = false;
            throw new InvalidOperationException("observer failed");
        };
    }

    [Fact]
    public async Task NewSession_ObserverThrowsAsTheSwitchStarts_IsReported_AndDoesNotLockTheComposer()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "draft";
        ThrowOnceWhen(vm, nameof(ChatViewModel.CanConfigure), () => !vm.CanConfigure);

        var failure = await Record.ExceptionAsync(() => vm.NewSessionCommand.ExecuteAsync(null));

        Assert.Null(failure);
        Assert.True(vm.SendCommand.CanExecute(null));
        Assert.True(vm.CanConfigure);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task OpenSession_ObserverThrowsAsTheSwitchStarts_IsReported_AndDoesNotLockTheComposer()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "draft";
        ThrowOnceWhen(vm, nameof(ChatViewModel.CanConfigure), () => !vm.CanConfigure);

        var failure = await Record.ExceptionAsync(() =>
            vm.OpenSessionCommand.ExecuteAsync(new SessionSummary("session-2", "/workspace", "Older chat", null)));

        Assert.Null(failure);
        Assert.True(vm.SendCommand.CanExecute(null));
        Assert.True(vm.CanConfigure);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task WorkspaceSwitch_ObserverThrowsAsTheSwitchStarts_IsReported_AndStillSwitches()
    {
        var first = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var second = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var factory = new SingleConnectionFactory(first);
        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService(), "/solution-a");
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        factory.ConnectHandler = _ => Task.FromResult<IAcpAgentConnection>(second);
        vm.InputText = "draft";
        ThrowOnceWhen(vm, nameof(ChatViewModel.CanConfigure), () => !vm.CanConfigure);

        services.SetWorkspaceRoot("/solution-b");

        await WaitUntilAsync(() => second.NewSessionCwds.Count == 1);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal("/solution-b", second.NewSessionCwds[0]);
        Assert.True(vm.SendCommand.CanExecute(null));
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task ShowHistory_ObserverThrowsAsTheHistoryOpens_IsReported_AndDoesNotStayLoading()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        ThrowOnceWhen(vm, nameof(ChatViewModel.IsHistoryLoading), () => vm.IsHistoryLoading);

        var failure = await Record.ExceptionAsync(() => vm.ShowHistoryCommand.ExecuteAsync(null));

        Assert.Null(failure);
        Assert.False(vm.IsHistoryLoading);
        Assert.Contains("observer failed", vm.HistoryError, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task Login_ObserverThrowsAsTheReconnectStarts_IsReported_AndDoesNotStayConnecting()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        ThrowOnceWhen(vm, nameof(ChatViewModel.IsConnecting), () => vm.IsConnecting);
        vm.InputText = "/login";

        var failure = await Record.ExceptionAsync(() => vm.SendAsync());

        Assert.Null(failure);
        Assert.False(vm.IsConnecting);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task AuthStateChange_ObserverThrowsAsTheReconnectStarts_IsReported_AndDoesNotStayConnecting()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        ThrowOnceWhen(vm, nameof(ChatViewModel.IsConnecting), () => vm.IsConnecting);

        auth.SetState(AuthState.SignedIn);

        await WaitUntilAsync(() => WasLogged(services, "observer failed"));
        Assert.False(vm.IsConnecting);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteControl_ObserverThrowsAsTheToggleStarts_IsReported_AndDoesNotStayBusy()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        ThrowOnceWhen(vm, nameof(ChatViewModel.IsRemoteControlBusy), () => vm.IsRemoteControlBusy);

        var failure = await Record.ExceptionAsync(() => vm.ToggleRemoteControlCommand.ExecuteAsync(null));

        Assert.Null(failure);
        Assert.False(vm.IsRemoteControlBusy);
        Assert.True(vm.ToggleRemoteControlCommand.CanExecute(null));
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task NewSessionFailure_IsShownAndLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        connection.NewSessionHandler = _ => Task.FromException<NewSessionResult>(new InvalidOperationException("session/new rejected"));

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Contains("session/new rejected", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "session/new rejected"));
    }

    [Fact]
    public async Task OpenSessionFailure_IsShownAndLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        connection.LoadSessionHandler = (_, _, _, _) => Task.FromException<NewSessionResult>(new InvalidOperationException("session/load rejected"));

        await vm.OpenSessionAsync(new SessionSummary("session-2", "/workspace", "Older chat", null));

        Assert.Contains("session/load rejected", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "session/load rejected"));
    }

    [Fact]
    public async Task HistoryFailure_IsShownAndLogged()
    {
        var connection = new RecordingAcpAgentConnection
        {
            ConfigOptions = Options(),
            ListSessionsHandler = (_, _) => Task.FromException<IReadOnlyList<SessionSummary>>(new InvalidOperationException("session/list rejected")),
        };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;

        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.Contains("session/list rejected", vm.HistoryError, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "session/list rejected"));
    }

    [Fact]
    public async Task RemoteControlFailure_IsShownAndLogged()
    {
        var connection = new RecordingAcpAgentConnection
        {
            ConfigOptions = Options(),
            RemoteControlHandler = _ => Task.FromException<RemoteControlState>(new InvalidOperationException("the launcher refused")),
        };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;

        await vm.ToggleRemoteControlCommand.ExecuteAsync(null);

        Assert.Contains("the launcher refused", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "the launcher refused"));
    }

    [Fact]
    public async Task SignInFailure_IsShownAndLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var auth = new RecordingAuthService(AuthState.SignedOut)
        {
            SignInHandler = (_, _) => Task.FromException(new InvalidOperationException("browser unavailable")),
        };
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Contains("browser unavailable", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "browser unavailable"));
    }

    [Fact]
    public async Task WorkspaceSwitchFailure_IsShownAndLogged()
    {
        var first = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var second = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var factory = new SingleConnectionFactory(first);
        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService(), "/solution-a");
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        factory.ConnectHandler = _ => Task.FromResult<IAcpAgentConnection>(second);
        vm.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) throw new InvalidOperationException("transcript observer failed");
        };

        services.SetWorkspaceRoot("/solution-b");

        await WaitUntilAsync(() => WasLogged(services, "transcript observer failed"));
        Assert.Contains("transcript observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentCloseFailure_IsLogged()
    {
        var first = new RecordingAcpAgentConnection { ConfigOptions = Options(), DisposeHandler = () => Task.FromException(new InvalidOperationException("agent would not exit")) };
        var second = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var factory = new SingleConnectionFactory(first);
        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService(), "/solution-a");
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        factory.ConnectHandler = _ => Task.FromResult<IAcpAgentConnection>(second);

        services.SetWorkspaceRoot("/solution-b");

        await WaitUntilAsync(() => WasLogged(services, "agent would not exit"));
    }

    [Fact]
    public async Task PlanReview_ObserverThrowsAsTheEndedRequestClosesTheCard_StillResolvesThePlan_AndIsLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        var (call, options) = PlanApprovalRequest();
        var request = connection.RaisePermissionRequested(call, options);
        var plan = vm.PendingPlan!;
        ThrowOnceWhen(vm, nameof(ChatViewModel.PendingPermission), () => vm.PendingPermission is null);

        request.Response.TrySetException(new OperationCanceledException("The turn was cancelled."));

        await WaitUntilAsync(() => WasLogged(services, "observer failed"));
        Assert.True(plan.IsResolved);
    }

    [Fact]
    public async Task PlanReview_ObserverThrowsAsANewerRequestSupersedesThePlan_ShowsTheNewerRequest_AndIsLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        var plan = vm.PendingPlan!;
        plan.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlanReviewViewModel.IsResolved) && plan.IsResolved)
                throw new InvalidOperationException("observer failed");
        };
        var edit = new ToolCallUpdate { ToolCallId = "tc-1", Title = "Edit a.cs", Status = ToolCallStatus.Pending };

        var failure = Record.Exception(() => connection.RaisePermissionRequested(edit,
            [new PermissionOption { OptionId = "allow", Label = "Allow", Outcome = PermissionOutcome.AllowOnce }]));

        Assert.Null(failure);
        Assert.NotNull(vm.PendingPermission);
        Assert.Equal("allow", Assert.Single(vm.PendingPermission!.Options).OptionId);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task Elicitation_ObserverThrowsAsTheEndedRequestClosesTheForm_IsLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        var request = connection.RaiseElicitationRequested("Pick a color", [new ElicitationField("q0", null, null, ElicitationFieldKind.Text, [])]);
        ThrowOnceWhen(vm, nameof(ChatViewModel.PendingElicitation), () => vm.PendingElicitation is null);

        request.Response.TrySetException(new OperationCanceledException("The turn was cancelled."));

        await WaitUntilAsync(() => WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task Elicitation_ObserverThrowsAsTheDeclinedFormCloses_StillAnswers_AndIsLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        var request = connection.RaiseElicitationRequested("Pick a color", [new ElicitationField("q0", null, null, ElicitationFieldKind.Text, [])]);
        ThrowOnceWhen(vm, nameof(ChatViewModel.PendingElicitation), () => vm.PendingElicitation is null);

        var failure = Record.Exception(() => vm.PendingElicitation!.DeclineCommand.Execute(null));

        Assert.Null(failure);
        await WithinAsync(request.Response.Task);
        Assert.Equal(ElicitationAction.Decline, (await request.Response.Task).Action);
        Assert.True(WasLogged(services, "observer failed"));
    }

    [Fact]
    public async Task Disconnect_ObserverThrowsAsTheConnectionIsReleased_StillClosesTheAgent_AndIsLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        ThrowOnceWhen(vm, nameof(ChatViewModel.SelectedModel), () => vm.SelectedModel is null);

        connection.RaiseDisconnected();

        await WaitUntilAsync(() => WasLogged(services, "observer failed"));
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task SignOut_ObserverThrowsAsTheConnectionIsReleased_StillClosesTheAgent_AndIsLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        ThrowOnceWhen(vm, nameof(ChatViewModel.SelectedModel), () => vm.SelectedModel is null);

        auth.SetState(AuthState.SignedOut);

        await WaitUntilAsync(() => WasLogged(services, "observer failed"));
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task Dispose_ObserverThrowsAsTheConnectionIsReleased_StillClosesTheAgent_AndIsLogged()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        var vm = new ChatViewModel(services);
        await vm.Initialization;
        ThrowOnceWhen(vm, nameof(ChatViewModel.SelectedModel), () => vm.SelectedModel is null);

        vm.Dispose();

        await WaitUntilAsync(() => WasLogged(services, "observer failed"));
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ActiveDocumentChange_ObserverThrows_IsReported_NotThrownAtTheHost()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = ServicesFor(connection);
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        ThrowOnceWhen(vm, nameof(ChatViewModel.HasActiveDocument), () => true);

        var failure = Record.Exception(() => services.SetHasActiveDocument(false));

        Assert.Null(failure);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(WasLogged(services, "observer failed"));
    }
}
