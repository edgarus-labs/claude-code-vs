using ClaudeCode.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ClaudeCode.Vsix.VsControl;

internal sealed class VsControlSessionRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, VsControlPipeServer> _servers = new ConcurrentDictionary<string, VsControlPipeServer>();
    private readonly string? _vsControlMcpExecutablePath;

    public VsControlSessionRegistry()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(ClaudeCodePackage).Assembly.Location);
        var candidate = assemblyDirectory is not null
            ? Path.Combine(assemblyDirectory, "VsControlMcp", "EdgarusLabs.ClaudeCode.VsControl.Mcp.exe")
            : null;
        _vsControlMcpExecutablePath = candidate is not null && File.Exists(candidate) ? candidate : null;
    }

    public bool IsAvailable => _vsControlMcpExecutablePath is not null;

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

    public void Dispose()
    {
        foreach (var correlationId in _servers.Keys)
        {
            EndSession(correlationId);
        }
    }
}
