using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// Provides an always-authenticated implementation of the authentication service that reports the user as permanently signed in, intended for scenarios where interactive login is unnecessary, such as testing or offline operation.
/// </summary>
internal sealed class AlwaysSignedInAuthService : IAcpAuthService
{
    /// <summary>
    /// Gets the current state.
    /// </summary>
    public AuthState CurrentState => AuthState.SignedIn;

    /// <summary>
    /// Occurs when state changed.
    /// </summary>
    public event EventHandler<AuthStateChangedEventArgs>? StateChanged { add { } remove { } }

    /// <summary>
    /// Asynchronously indicates that the current user is always considered signed in by returning a completed task with a value of true.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    /// Asynchronously completes immediately as a no-op sign-in, without authenticating the user or reporting progress.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <param name="progress">The progress.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) => Task.CompletedTask;

    /// <summary>
    /// Asynchronously signs out the current user, returning a completed task as no session-specific cleanup is required by this implementation.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Returns a completed task.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <param name="progress">The progress.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the auth command outcome.</returns>
    public Task<AuthCommandOutcome> LaunchInteractiveLoginAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        Task.FromResult(new AuthCommandOutcome(true, "Signed in."));

    /// <summary>
    /// Returns a completed task containing a successful authentication command outcome, signaling that the interactive sign-out has been acknowledged without performing any interactive logout flow.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the auth command outcome.</returns>
    public Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AuthCommandOutcome(true, "Signed out."));
}
