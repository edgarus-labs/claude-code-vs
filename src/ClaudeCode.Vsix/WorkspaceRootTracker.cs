using System;

namespace ClaudeCode.Vsix;

internal sealed class WorkspaceRootTracker
{
    private volatile string? _root;

    public string? Root => _root;

    public event EventHandler? Changed;

    public void Update(string? root)
    {
        _root = root;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
