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
}
