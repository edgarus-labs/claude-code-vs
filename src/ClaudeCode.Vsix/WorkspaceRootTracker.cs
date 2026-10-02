using System;

namespace ClaudeCode.Vsix;

internal sealed class WorkspaceRootTracker
{
    private volatile string? _root;

    /// <summary>
    /// Gets the root.
    /// </summary>
    public string? Root => _root;

    /// <summary>
    /// Occurs when changed.
    /// </summary>
    public event EventHandler? Changed;

    public void Update(string? root)
    {
        _root = root;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
