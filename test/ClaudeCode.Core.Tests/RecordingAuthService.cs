using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

internal sealed class RecordingAuthService : IAcpAuthService
{
    public RecordingAuthService(AuthState initialState = AuthState.SignedOut)
    {
        CurrentState = initialState;
    }

    /// <summary>
    /// Gets or sets the current state.
    /// </summary>
    public AuthState CurrentState { get; private set; }

    /// <summary>
    /// Occurs when state changed.
    /// </summary>
    public event EventHandler<AuthStateChangedEventArgs>? StateChanged;

    public void SetState(AuthState state, string? detail = null)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(state, detail));
    }

    /// <summary>
    /// Gets or sets the is signed in handler.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? IsSignedInHandler { get; set; }

    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) =>
        IsSignedInHandler?.Invoke(cancellationToken) ?? Task.FromResult(CurrentState == AuthState.SignedIn);

    public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) => Task.CompletedTask;

    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Gets or sets the login handler.
    /// </summary>
    public Func<CancellationToken, IProgress<string>?, Task<AuthCommandOutcome>>? LoginHandler { get; set; }

    /// <summary>
    /// Gets or sets the login call count.
    /// </summary>
    public int LoginCallCount { get; private set; }

    public Task<AuthCommandOutcome> LaunchInteractiveLoginAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        LoginCallCount++;
        return LoginHandler?.Invoke(cancellationToken, progress) ?? Task.FromResult(new AuthCommandOutcome(true, "Signed in."));
    }

    /// <summary>
    /// Gets or sets the logout handler.
    /// </summary>
    public Func<CancellationToken, Task<AuthCommandOutcome>>? LogoutHandler { get; set; }

    /// <summary>
    /// Gets or sets the logout call count.
    /// </summary>
    public int LogoutCallCount { get; private set; }

    public Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken)
    {
        LogoutCallCount++;
        return LogoutHandler?.Invoke(cancellationToken) ?? Task.FromResult(new AuthCommandOutcome(true, "Signed out."));
    }
}
