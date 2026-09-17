using ClaudeCode.Contracts;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

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

    public McpServerConfig StartSession(out string correlationId)
    {
        if (_vsControlMcpExecutablePath is null)
        {
            throw new InvalidOperationException("EdgarusLabs.ClaudeCode.VsControl.Mcp.exe was not found; call IsAvailable first.");
        }

        correlationId = Guid.NewGuid().ToString("N");
        var pipeName = $"ClaudeCodeVs.Control.{Process.GetCurrentProcess().Id}.{correlationId}";

        var server = new VsControlPipeServer(pipeName);
        _servers[correlationId] = server;
        server.Start();

        return new McpServerConfig("visual-studio", _vsControlMcpExecutablePath, new[] { "--pipe", pipeName });
    }

    public void EndSession(string correlationId)
    {
        if (_servers.TryRemove(correlationId, out var server))
        {
            _ = server.DisposeAsync();
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
