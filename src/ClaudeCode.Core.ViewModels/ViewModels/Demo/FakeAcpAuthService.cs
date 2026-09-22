using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

public sealed class FakeAcpAuthService : IAcpAuthService
{
    public AuthState CurrentState { get; private set; } = AuthState.SignedIn;

    public event EventHandler<AuthStateChangedEventArgs>? StateChanged;

    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) => Task.FromResult(CurrentState == AuthState.SignedIn);

    public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        progress?.Report("Signed in (demo mode).");
        CurrentState = AuthState.SignedIn;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedIn));

        return Task.CompletedTask;
    }

    public Task SignOutAsync(CancellationToken cancellationToken)
    {
        CurrentState = AuthState.SignedOut;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedOut));

        return Task.CompletedTask;
    }

    public Task<AuthCommandOutcome> LaunchInteractiveLoginAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        progress?.Report("Signed in (demo mode).");
        CurrentState = AuthState.SignedIn;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedIn));

        return Task.FromResult(new AuthCommandOutcome(true, "Signed in to Claude (demo mode)."));
    }

    public Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken)
    {
        CurrentState = AuthState.SignedOut;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedOut));

        return Task.FromResult(new AuthCommandOutcome(true, "Signed out of Claude (demo mode)."));
    }
}
