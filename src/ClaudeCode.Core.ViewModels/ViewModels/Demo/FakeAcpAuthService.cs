using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

/// <summary>
/// Provides a fake, in-memory implementation of the ACP authentication service for testing sign-in, sign-out, and authentication state scenarios without interactive login.
/// </summary>
public sealed class FakeAcpAuthService : IAcpAuthService
{
    /// <summary>
    /// Gets or sets the current state.
    /// </summary>
    public AuthState CurrentState { get; private set; } = AuthState.SignedIn;

    /// <summary>
    /// Occurs when state changed.
    /// </summary>
    public event EventHandler<AuthStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Asynchronously determines whether the current authentication state is signed in, returning a completed task with the result.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) => Task.FromResult(CurrentState == AuthState.SignedIn);

    /// <summary>
    /// Signs in the user in demo mode by setting the authentication state to signed in, reporting progress, and raising the state-changed event.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <param name="progress">The progress.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        progress?.Report("Signed in (demo mode).");
        CurrentState = AuthState.SignedIn;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedIn));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Signs out the current user by setting the authentication state to signed out and raising the StateChanged event.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SignOutAsync(CancellationToken cancellationToken)
    {
        CurrentState = AuthState.SignedOut;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedOut));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Simulates an interactive sign-in flow in demo mode by reporting progress, transitioning the authentication state to signed in, raising the state-changed event, and returning a successful outcome.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <param name="progress">The progress.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the auth command outcome.</returns>
    public Task<AuthCommandOutcome> LaunchInteractiveLoginAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        progress?.Report("Signed in (demo mode).");
        CurrentState = AuthState.SignedIn;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedIn));

        return Task.FromResult(new AuthCommandOutcome(true, "Signed in to Claude (demo mode)."));
    }

    /// <summary>
    /// Signs the user out of the simulated Claude session by setting the authentication state to signed out, raising the StateChanged event, and returning a successful outcome.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the auth command outcome.</returns>
    public Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken)
    {
        CurrentState = AuthState.SignedOut;
        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedOut));

        return Task.FromResult(new AuthCommandOutcome(true, "Signed out of Claude (demo mode)."));
    }
}
