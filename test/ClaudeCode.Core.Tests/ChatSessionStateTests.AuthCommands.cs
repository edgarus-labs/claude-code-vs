using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

// Covers issue #34 (client-side /login and /logout in the chat sidebar): local command listing,
// interception so nothing reaches session/prompt, login success/failure/cancellation, logout with
// confirmation and state reset, and the unresolved-adapter path.
public sealed partial class ChatSessionStateTests
{
    private static ChatViewModel CreateWithAuth(RecordingAcpAgentConnection connection, RecordingAuthService auth, out StubChatSessionServices services)
    {
        services = new StubChatSessionServices(new SingleConnectionFactory(connection), auth);
        return new ChatViewModel(services);
    }

    [Fact]
    public async Task SignedOut_StillListsLoginAndLogoutInThePopup_EvenThoughOrdinarySendIsBlocked()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;

        vm.InputText = "/";
        Assert.True(vm.NeedsAuthentication);
        Assert.Equal(ClientSlashCommandNames, vm.SlashSuggestions.Select(c => c.Name));
        Assert.True(vm.AreSlashSuggestionsVisible);

        // An ordinary message is still refused while signed out...
        vm.InputText = "hello";
        Assert.False(vm.SendCommand.CanExecute(null));

        // ...but /login is reachable, which is the entire point of the feature.
        vm.InputText = "/login";
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task SendingLogin_NeverReachesTheAgentAsAPrompt()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        var login = new TaskCompletionSource<AuthCommandOutcome>();
        auth.LoginHandler = (_, _) => login.Task;

        vm.InputText = "/login";
        var sending = vm.SendAsync();

        Assert.Equal(string.Empty, vm.InputText);
        Assert.Empty(connection.Prompts);
        Assert.Empty(vm.Messages);
        Assert.True(vm.IsAuthCommandRunning);

        login.SetResult(new AuthCommandOutcome(true, "Signed in to Claude."));
        await sending;

        Assert.Empty(connection.Prompts);
    }

    [Fact]
    public async Task SendingLogout_NeverReachesTheAgentAsAPrompt()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        services.ConfirmSignOutResponse = true;

        vm.InputText = "/logout";
        await vm.SendAsync();

        Assert.Empty(connection.Prompts);
        Assert.Empty(vm.Messages);
        Assert.Equal(1, auth.LogoutCallCount);
    }

    [Fact]
    public async Task Login_Success_UpdatesStatusAndSignsIn_AndEstablishesASession()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        Assert.True(vm.NeedsAuthentication);

        auth.LoginHandler = (_, progress) =>
        {
            progress?.Report("Opening a console…");
            auth.SetState(AuthState.SignedIn);
            return Task.FromResult(new AuthCommandOutcome(true, "Signed in to Claude."));
        };

        vm.InputText = "/login";
        await vm.SendAsync();
        await vm.Initialization;

        Assert.False(vm.IsAuthCommandRunning);
        Assert.False(vm.NeedsAuthentication);
        Assert.True(vm.IsSignedIn);
        Assert.Equal("Signed in to Claude.", vm.StatusMessage);
        Assert.Single(connection.NewSessionCwds);
    }

    [Fact]
    public async Task Login_Failure_ShowsActionableMessage_AndNeverStaysStuckSigningIn()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        auth.LoginHandler = (_, _) => Task.FromResult(new AuthCommandOutcome(false, "Sign-in was not completed (exit code 1)."));

        vm.InputText = "/login";
        await vm.SendAsync();

        Assert.False(vm.IsAuthCommandRunning);
        Assert.True(vm.NeedsAuthentication);
        Assert.Equal("Sign-in was not completed (exit code 1).", vm.StatusMessage);
    }

    [Fact]
    public async Task Login_Cancelled_RevertsToSigningInFalse_WithAnActionableMessage()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        var started = new TaskCompletionSource<bool>();
        auth.LoginHandler = async (token, _) =>
        {
            started.SetResult(true);
            var never = new TaskCompletionSource<bool>();
            using (token.Register(() => never.TrySetCanceled(token)))
            {
                await never.Task;
            }

            return new AuthCommandOutcome(false, "unreachable");
        };

        vm.InputText = "/login";
        var sending = vm.SendAsync();
        await started.Task;
        Assert.True(vm.IsAuthCommandRunning);
        Assert.True(vm.CancelAuthCommand.CanExecute(null));

        vm.CancelAuthCommand.Execute(null);
        await sending;

        Assert.False(vm.IsAuthCommandRunning);
        Assert.True(vm.NeedsAuthentication);
        Assert.Equal("Sign-in cancelled.", vm.StatusMessage);
    }

    [Fact]
    public async Task Logout_Declined_NeverCallsTheAdapter_AndKeepsCurrentState()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        services.ConfirmSignOutResponse = false;

        vm.InputText = "/logout";
        await vm.SendAsync();

        Assert.Equal(0, auth.LogoutCallCount);
        Assert.True(vm.IsSignedIn);
        Assert.Equal(AuthState.SignedIn, auth.CurrentState);
    }

    [Fact]
    public async Task Logout_Confirmed_Succeeds_SignsOutAndClosesTheActiveSession()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        Assert.Single(connection.NewSessionCwds); // the active session that must be closed below
        services.ConfirmSignOutResponse = true;
        auth.LogoutHandler = _ =>
        {
            const string detail = "Signed out of Claude Code. This affects the CLI, VS Code and other clients on this machine, not only Visual Studio.";
            auth.SetState(AuthState.SignedOut, detail);
            return Task.FromResult(new AuthCommandOutcome(true, detail));
        };

        vm.InputText = "/logout";
        await vm.SendAsync();
        await WaitUntilAsync(() => connection.DisposeCount == 1);

        Assert.False(vm.IsAuthCommandRunning);
        Assert.True(vm.NeedsAuthentication);
        Assert.False(vm.IsSignedIn);
        Assert.Contains("Signed out", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Single(services.ConfirmSignOutRequests);
    }

    [Fact]
    public async Task Logout_Failure_KeepsTheUserSignedIn()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        services.ConfirmSignOutResponse = true;
        auth.LogoutHandler = _ => Task.FromResult(new AuthCommandOutcome(false, "Sign-out did not complete (exit code 1)."));

        vm.InputText = "/logout";
        await vm.SendAsync();

        Assert.Equal("Sign-out did not complete (exit code 1).", vm.StatusMessage);
        Assert.True(vm.IsSignedIn);
    }

    [Fact]
    public async Task Logout_WhileATurnIsInFlight_IsRefused_InsteadOfTearingDownTheSession()
    {
        var connection = new RecordingAcpAgentConnection();
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;

        vm.InputText = "hello";
        var sending = vm.SendAsync();
        await WaitUntilAsync(() => vm.IsBusy);

        vm.InputText = "/logout";
        Assert.False(vm.SendCommand.CanExecute(null));
        await vm.SendAsync();

        Assert.Empty(services.ConfirmSignOutRequests);
        Assert.Equal(0, auth.LogoutCallCount);
        Assert.Equal("/logout", vm.InputText);

        turn.SetResult(true);
        await sending;
    }

    [Theory]
    [InlineData("/logins")]
    [InlineData("/loginfoo")]
    [InlineData("/logout-all")]
    [InlineData("please /login")]
    public async Task TextThatOnlyResemblesACommand_IsSentToTheAgent(string text)
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;

        vm.InputText = text;
        await vm.SendAsync();

        Assert.Single(connection.Prompts);
        Assert.Equal(0, auth.LoginCallCount);
        Assert.Equal(0, auth.LogoutCallCount);
        Assert.Empty(services.ConfirmSignOutRequests);
    }

    [Theory]
    [InlineData("/LOGIN")]
    [InlineData("  /login  ")]
    [InlineData("/login extra words")]
    public async Task LoginCommand_IsMatchedCaseInsensitively_AndIgnoresArguments(string text)
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        auth.LoginHandler = (_, _) => Task.FromResult(new AuthCommandOutcome(false, "not completed"));

        vm.InputText = text;
        await vm.SendAsync();

        Assert.Equal(1, auth.LoginCallCount);
        Assert.Empty(connection.Prompts);
    }

    [Fact]
    public async Task WhileLoginIsRunning_ASecondLoginOrLogoutIsRefused()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        var login = new TaskCompletionSource<AuthCommandOutcome>();
        auth.LoginHandler = (_, _) => login.Task;

        vm.InputText = "/login";
        var sending = vm.SendAsync();
        Assert.True(vm.IsAuthCommandRunning);

        vm.InputText = "/login";
        Assert.False(vm.SendCommand.CanExecute(null));
        await vm.SendAsync();
        vm.InputText = "/logout";
        Assert.False(vm.SendCommand.CanExecute(null));
        await vm.SendAsync();

        Assert.Equal(1, auth.LoginCallCount);
        Assert.Equal(0, auth.LogoutCallCount);
        Assert.Empty(services.ConfirmSignOutRequests);

        login.SetResult(new AuthCommandOutcome(false, "not completed"));
        await sending;
    }

    [Fact]
    public async Task Login_ServiceThrows_ReportsTheFailure_AndClearsTheBusyFlag()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        auth.LoginHandler = (_, _) => throw new InvalidOperationException("boom");

        vm.InputText = "/login";
        await vm.SendAsync();

        Assert.Equal("Sign-in failed: boom", vm.StatusMessage);
        Assert.False(vm.IsAuthCommandRunning);
        Assert.False(vm.CancelAuthCommand.CanExecute(null));
    }

    [Fact]
    public async Task Login_CancellationTheUserDidNotRequest_IsReportedAsAFailure()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        auth.LoginHandler = (_, _) => Task.FromException<AuthCommandOutcome>(new OperationCanceledException("inner timeout"));

        vm.InputText = "/login";
        await vm.SendAsync();

        Assert.Equal("Sign-in failed: inner timeout", vm.StatusMessage);
    }

    [Fact]
    public async Task Login_Succeeds_ButConnectingFails_ShowsTheConnectFailure()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        connection.NewSessionHandler = _ => throw new InvalidOperationException("adapter crashed");
        auth.LoginHandler = (_, _) =>
        {
            auth.IsSignedInHandler = _ => Task.FromResult(true);
            return Task.FromResult(new AuthCommandOutcome(true, "Signed in to Claude."));
        };

        vm.InputText = "/login";
        await vm.SendAsync();

        Assert.Contains("adapter crashed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(vm.IsAuthCommandRunning);
    }

    [Fact]
    public async Task Logout_ConfirmationDialogThrows_ReportsTheFailure_AndNeverCallsTheAdapter()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        services.ConfirmSignOutHandler = _ => throw new InvalidOperationException("no shell");

        vm.InputText = "/logout";
        await vm.SendAsync();

        Assert.Equal("Sign-out failed: no shell", vm.StatusMessage);
        Assert.Equal(0, auth.LogoutCallCount);
        Assert.False(vm.IsAuthCommandRunning);
    }

    [Fact]
    public async Task Logout_Cancelled_ReportsCancellation_AndKeepsTheUserSignedIn()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        services.ConfirmSignOutResponse = true;
        var started = new TaskCompletionSource<bool>();
        auth.LogoutHandler = async token =>
        {
            started.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return new AuthCommandOutcome(true, "unreachable");
        };

        vm.InputText = "/logout";
        var sending = vm.SendAsync();
        await started.Task;
        vm.CancelAuthCommand.Execute(null);
        await sending;

        Assert.Equal("Sign-out cancelled.", vm.StatusMessage);
        Assert.True(vm.IsSignedIn);
        Assert.False(vm.IsAuthCommandRunning);
    }

    [Fact]
    public async Task Dispose_WhileLoginIsRunning_CancelsIt_WithoutReportingAnything()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        CancellationToken observed = default;
        auth.LoginHandler = async (token, _) =>
        {
            observed = token;
            await Task.Delay(Timeout.Infinite, token);
            return new AuthCommandOutcome(true, "unreachable");
        };

        vm.InputText = "/login";
        var sending = vm.SendAsync();
        string? before = vm.StatusMessage;
        vm.Dispose();
        await sending;

        Assert.True(observed.IsCancellationRequested);
        Assert.Equal(before, vm.StatusMessage);
    }

    [Fact]
    public async Task DemoServices_NeverConfirmAMachineWideSignOut()
    {
        var services = new ClaudeCode.Core.ViewModels.Demo.NullChatSessionServices();

        Assert.False(await services.ConfirmSignOutEverywhereAsync(CancellationToken.None));
    }
}
