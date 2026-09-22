using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

public interface IAcpAuthService
{
    AuthState CurrentState { get; }

    event EventHandler<AuthStateChangedEventArgs> StateChanged;

    Task<bool> IsSignedInAsync(CancellationToken cancellationToken);

    Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null);

    Task SignOutAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs the adapter's native interactive login in a visible console so the user can complete
    /// OAuth in their browser, then re-checks native status. Implementations must not read or store
    /// credentials; only the process exit code and the status probe cross the boundary. No fixed
    /// timeout, since the flow waits on the user.
    /// <para>Success is decided by the status probe, which updates <see cref="CurrentState"/> and
    /// raises <see cref="StateChanged"/>. Failures the user can act on (adapter missing, console not
    /// started, sign-in not confirmed) are returned as an unsuccessful outcome, not thrown.
    /// Cancellation throws <see cref="OperationCanceledException"/> and leaves
    /// <see cref="CurrentState"/> unchanged.</para>
    /// </summary>
    Task<AuthCommandOutcome> LaunchInteractiveLoginAsync(CancellationToken cancellationToken, IProgress<string>? progress = null);

    /// <summary>
    /// Runs the adapter's native logout in a hidden process (its output is never shown), which signs
    /// the user out of Claude Code everywhere on this machine (CLI, VS Code, other clients), not only
    /// this session. Does not ask the user itself; callers confirm first. There is no progress
    /// parameter because nothing interactive happens.
    /// <para>On success <see cref="CurrentState"/> becomes <see cref="AuthState.SignedOut"/> and
    /// <see cref="StateChanged"/> is raised; on failure the state reflects the follow-up status probe.
    /// Failures are returned as an unsuccessful outcome, not thrown. Cancellation throws
    /// <see cref="OperationCanceledException"/> and leaves <see cref="CurrentState"/> unchanged.</para>
    /// </summary>
    Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken);
}
