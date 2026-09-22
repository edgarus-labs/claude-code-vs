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
    public async Task Login_UnresolvedAdapter_ShowsInstallGuidance_AndStaysSignedOut()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedOut);
        using var vm = CreateWithAuth(connection, auth, out _);
        await vm.Initialization;
        const string guidance = "Install @agentclientprotocol/claude-agent-acp and Node.js 22 or newer, "
            + "or configure its ACP executable path in Tools > Options > Claude Code.";
        auth.LoginHandler = (_, _) => Task.FromResult(new AuthCommandOutcome(false, guidance));

        vm.InputText = "/login";
        await vm.SendAsync();

        Assert.False(vm.IsAuthCommandRunning);
        Assert.True(vm.NeedsAuthentication);
        Assert.Equal(guidance, vm.StatusMessage);
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
    public async Task Logout_UnresolvedAdapter_ShowsInstallGuidance()
    {
        var connection = new RecordingAcpAgentConnection();
        var auth = new RecordingAuthService(AuthState.SignedIn);
        using var vm = CreateWithAuth(connection, auth, out var services);
        await vm.Initialization;
        services.ConfirmSignOutResponse = true;
        const string guidance = "Install @agentclientprotocol/claude-agent-acp and Node.js 22 or newer, "
            + "or configure its ACP executable path in Tools > Options > Claude Code.";
        auth.LogoutHandler = _ => Task.FromResult(new AuthCommandOutcome(false, guidance));

        vm.InputText = "/logout";
        await vm.SendAsync();

        Assert.Equal(guidance, vm.StatusMessage);
        Assert.True(vm.IsSignedIn); // unchanged: the logout never actually happened
    }
}
