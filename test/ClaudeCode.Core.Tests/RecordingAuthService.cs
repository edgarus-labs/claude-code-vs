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
    /// <summary>
    /// Initializes a new instance of the RecordingAuthService class with the specified initial authentication state, defaulting to signed out.
    /// </summary>
    /// <param name="initialState">The initial state.</param>
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

    /// <summary>
    /// Updates the current authentication state and raises the StateChanged event with the new state and an optional detail message.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="detail">The detail.</param>
    public void SetState(AuthState state, string? detail = null)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(state, detail));
    }

    /// <summary>
    /// Gets or sets the is signed in handler.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? IsSignedInHandler { get; set; }

    /// <summary>
    /// Asynchronously determines whether the user is currently signed in by invoking the configured sign-in handler if one is set, otherwise returning the result based on the current authentication state.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) =>
        IsSignedInHandler?.Invoke(cancellationToken) ?? Task.FromResult(CurrentState == AuthState.SignedIn);

    /// <summary>
    /// Gets or sets the sign in handler.
    /// </summary>
    public Func<CancellationToken, IProgress<string>?, Task>? SignInHandler { get; set; }

    /// <summary>
    /// Asynchronously invokes the configured sign-in handler, if one is set, and otherwise returns a completed task.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <param name="progress">The progress.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        SignInHandler?.Invoke(cancellationToken, progress) ?? Task.CompletedTask;

    /// <summary>
    /// Asynchronously completes the sign-out operation without performing any session or token cleanup, returning an already completed task.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Gets or sets the login handler.
    /// </summary>
    public Func<CancellationToken, IProgress<string>?, Task<AuthCommandOutcome>>? LoginHandler { get; set; }

    /// <summary>
    /// Gets or sets the login call count.
    /// </summary>
    public int LoginCallCount { get; private set; }

    /// <summary>
    /// Asynchronously invokes the configured interactive login handler, increments the login call counter, and returns a successful outcome with a &quot;Signed in.&quot; message when no handler is set.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <param name="progress">The progress.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the auth command outcome.</returns>
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

    /// <summary>
    /// Asynchronously launches an interactive sign-out flow by invoking the configured logout handler, or returning a successful outcome when no handler is set, while tracking the number of logout invocations.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the auth command outcome.</returns>
    public Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken)
    {
        LogoutCallCount++;
        return LogoutHandler?.Invoke(cancellationToken) ?? Task.FromResult(new AuthCommandOutcome(true, "Signed out."));
    }
}
