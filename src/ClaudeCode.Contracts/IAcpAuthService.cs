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
    /// Runs the adapter's bundled CLI login (<c>--cli auth login --claudeai</c>) in a visible
    /// console so the user can complete OAuth in their browser, then re-checks native status. Never
    /// reads or stores credentials; only the process exit code and the existing status probe cross
    /// the boundary. Cancellable, with no fixed timeout, since the flow waits on the user.
    /// </summary>
    Task<AuthCommandOutcome> LaunchInteractiveLoginAsync(CancellationToken cancellationToken, IProgress<string>? progress = null);

    /// <summary>
    /// Runs the adapter's bundled CLI logout (<c>--cli auth logout</c>), which signs the user out of
    /// Claude Code everywhere on this machine (CLI, VS Code, other clients), not only this session.
    /// </summary>
    Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken);
}
