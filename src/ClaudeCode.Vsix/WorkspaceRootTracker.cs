using System;

namespace ClaudeCode.Vsix;

/// <summary>Holds the workspace root the shell last reported and announces every solution
/// open/close that may have changed it. The root is cached rather than pulled from the shell so
/// <c>GetWorkspaceRoot()</c> never has to block on the UI thread.</summary>
internal sealed class WorkspaceRootTracker
{
    public string? Root { get; private set; }

    /// <summary>Raised on the UI thread after <see cref="Root"/> is updated. Deliberately fires for
    /// every solution event, including one that reopens the same root: only the subscriber knows
    /// whether an unchanged root still matters to it.</summary>
    public event EventHandler? Changed;

    public void Update(string? root)
    {
        Root = root;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
