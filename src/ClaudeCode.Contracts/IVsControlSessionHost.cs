namespace ClaudeCode.Contracts;

/// <summary>
/// The host-side factory for per-session Visual Studio control MCP servers.
/// </summary>
public interface IVsControlSessionHost
{
    /// <summary>Whether the VS-control MCP sidecar is deployed and sessions can be started.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Starts a control server sandboxed to <paramref name="workspaceRoot"/> and returns the MCP
    /// server configuration the agent must be given to reach it. A null or empty
    /// <paramref name="workspaceRoot"/> denies every path-taking tool.
    /// </summary>
    McpServerConfig StartSession(string? workspaceRoot, out string correlationId);

    /// <summary>Tears down the server started under <paramref name="correlationId"/>. Idempotent.</summary>
    void EndSession(string correlationId);
}
