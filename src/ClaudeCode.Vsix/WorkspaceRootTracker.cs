using System;

namespace ClaudeCode.Vsix;

/// <summary>Holds the workspace root the shell last reported and announces every solution
/// open/close that may have changed it. The root is cached rather than pulled from the shell so
/// <c>GetWorkspaceRoot()</c> never has to block on the UI thread.</summary>
internal sealed class WorkspaceRootTracker
{
    // Written on the UI thread from the solution events, but read from background threads: the
    // connection factory resolves the agent's working directory off the UI thread at connect time,
    // and a stale read there spawns the agent in the previous project's directory.
    private volatile string? _root;

    public string? Root => _root;

    /// <summary>Raised on the UI thread after <see cref="Root"/> is updated. Deliberately fires for
    /// every solution event, including one that reopens the same root: only the subscriber knows
    /// whether an unchanged root still matters to it.</summary>
    public event EventHandler? Changed;

    public void Update(string? root)
    {
        _root = root;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
