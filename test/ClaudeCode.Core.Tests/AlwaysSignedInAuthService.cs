using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

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

    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) => Task.CompletedTask;

    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Asynchronously launches an interactive login flow and returns an AuthCommandOutcome indicating a successful sign‑in.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <param name="progress">The progress.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the auth command outcome.</returns>
    public Task<AuthCommandOutcome> LaunchInteractiveLoginAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        Task.FromResult(new AuthCommandOutcome(true, "Signed in."));

    public Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AuthCommandOutcome(true, "Signed out."));
}
