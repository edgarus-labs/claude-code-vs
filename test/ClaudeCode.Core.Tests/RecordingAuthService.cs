using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

/// <summary>Configurable <see cref="IAcpAuthService"/> double for exercising /login and /logout:
/// unlike <see cref="AlwaysSignedInAuthService"/>, this one actually tracks state and raises
/// <see cref="StateChanged"/>, mirroring how the real service's status probe and command results
/// drive <c>ChatViewModel</c>'s reconnect/disconnect plumbing.</summary>
internal sealed class RecordingAuthService : IAcpAuthService
{
    public RecordingAuthService(AuthState initialState = AuthState.SignedOut)
    {
        CurrentState = initialState;
    }

    public AuthState CurrentState { get; private set; }

    public event EventHandler<AuthStateChangedEventArgs>? StateChanged;

    public void SetState(AuthState state, string? detail = null)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(state, detail));
    }

    public Func<CancellationToken, Task<bool>>? IsSignedInHandler { get; set; }

    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) =>
        IsSignedInHandler?.Invoke(cancellationToken) ?? Task.FromResult(CurrentState == AuthState.SignedIn);

    public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) => Task.CompletedTask;

    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Func<CancellationToken, IProgress<string>?, Task<AuthCommandOutcome>>? LoginHandler { get; set; }

    public int LoginCallCount { get; private set; }

    public Task<AuthCommandOutcome> LaunchInteractiveLoginAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        LoginCallCount++;
        return LoginHandler?.Invoke(cancellationToken, progress) ?? Task.FromResult(new AuthCommandOutcome(true, "Signed in."));
    }

    public Func<CancellationToken, Task<AuthCommandOutcome>>? LogoutHandler { get; set; }

    public int LogoutCallCount { get; private set; }

    public Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken)
    {
        LogoutCallCount++;
        return LogoutHandler?.Invoke(cancellationToken) ?? Task.FromResult(new AuthCommandOutcome(true, "Signed out."));
    }
}
