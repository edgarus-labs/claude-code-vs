namespace ClaudeCode.Contracts;

/// <summary>
/// The host-side factory for per-session Visual Studio control MCP servers. Implemented by the
/// VSIX's session registry; abstracted here so the connection decorator that drives its lifetime
/// can be exercised without a Visual Studio host.
/// </summary>
public interface IVsControlSessionHost
{
    /// <summary>Whether the VS-control MCP sidecar is deployed and sessions can be started.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Starts a control server sandboxed to <paramref name="workspaceRoot"/> and returns the MCP
    /// server configuration the agent must be given to reach it. A null or empty
    /// <paramref name="workspaceRoot"/> denies every path-taking tool rather than widening the sandbox.
    /// </summary>
    McpServerConfig StartSession(string? workspaceRoot, out string correlationId);

    /// <summary>Tears down the server started under <paramref name="correlationId"/>. Idempotent.</summary>
    void EndSession(string correlationId);
}
