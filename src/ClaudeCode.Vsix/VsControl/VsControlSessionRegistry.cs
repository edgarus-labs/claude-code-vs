using ClaudeCode.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ClaudeCode.Vsix.VsControl;

/// <summary>
/// Represents a control session registry that manages Visual Studio control servers and provides session management capabilities.
/// </summary>
internal sealed class VsControlSessionRegistry : IVsControlSessionHost, IDisposable
{
    private readonly ConcurrentDictionary<string, VsControlPipeServer> _servers = new ConcurrentDictionary<string, VsControlPipeServer>();
    private readonly string? _vsControlMcpExecutablePath;

    /// <summary>
    /// Initializes a new instance of the VsControlSessionRegistry class by locating and validating the path to the VsControlMcp executable.
    /// </summary>
    public VsControlSessionRegistry()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(ClaudeCodePackage).Assembly.Location);
        var candidate = assemblyDirectory is not null
            ? Path.Combine(assemblyDirectory, "VsControlMcp", "EdgarusLabs.ClaudeCode.VsControl.Mcp.exe")
            : null;
        _vsControlMcpExecutablePath = candidate is not null && File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Gets a value indicating whether is available.
    /// </summary>
    public bool IsAvailable => _vsControlMcpExecutablePath is not null;

    /// <summary>
    /// Starts a new session by initializing a named pipe server, generating a correlation ID and security token, and returns the configuration for the MCP server.
    /// </summary>
    /// <param name="workspaceRoot">The workspace root.</param>
    /// <param name="correlationId">The unique identifier of the correlation.</param>
    /// <returns>The mcp server config result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when an error occurs during execution.</exception>
    public McpServerConfig StartSession(string? workspaceRoot, out string correlationId)
    {
        if (_vsControlMcpExecutablePath is null)
        {
            throw new InvalidOperationException("EdgarusLabs.ClaudeCode.VsControl.Mcp.exe was not found; call IsAvailable first.");
        }

        correlationId = Guid.NewGuid().ToString("N");
        var pipeName = $"ClaudeCodeVs.Control.{Process.GetCurrentProcess().Id}.{correlationId}";
        var tokenBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(tokenBytes);
        }
        var token = Convert.ToBase64String(tokenBytes);

        var server = new VsControlPipeServer(pipeName, workspaceRoot, token);
        _servers[correlationId] = server;
        server.Start();

        var env = new Dictionary<string, string> { ["CLAUDECODE_VSCONTROL_TOKEN"] = token };
        return new McpServerConfig("visual-studio", _vsControlMcpExecutablePath, new[] { "--pipe", pipeName }, env);
    }

    /// <summary>
    /// Ends the session associated with the specified correlation ID and disposes of the corresponding server asynchronously.
    /// </summary>
    /// <param name="correlationId">The unique identifier of the correlation.</param>
    public void EndSession(string correlationId)
    {
        if (_servers.TryRemove(correlationId, out var server))
        {
            // AsTask() consumes the ValueTask exactly once immediately (satisfying CA2012's "must be
            // used" contract) while keeping teardown fire-and-forget: EndSession is called from
            // synchronous cleanup paths that must not block on the pipe server's shutdown grace period.
            _ = server.DisposeAsync().AsTask();
        }
    }

    /// <summary>
    /// Ends all active sessions by terminating each server connection.
    /// </summary>
    public void Dispose()
    {
        foreach (var correlationId in _servers.Keys)
        {
            EndSession(correlationId);
        }
    }
}
