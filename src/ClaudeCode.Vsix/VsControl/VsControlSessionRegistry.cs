using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using ClaudeCode.Contracts;

namespace ClaudeCode.Vsix.VsControl
{
    /// <summary>
    /// Mints one <see cref="VsControlPipeServer"/> per active ACP session and builds the "visual-studio"
    /// <see cref="McpServerConfig"/> entry merged into every `session/new` call by
    /// <see cref="Connections.VsControlInjectingConnection"/>.
    /// </summary>
    /// <remarks>
    /// Pipe names use a locally-minted correlation id rather than the ACP protocol's own sessionId: the
    /// mcpServers list (and therefore the pipe name embedded in its `--pipe` argument) has to be sent as
    /// part of the `session/new` REQUEST, before the agent's `session/new` RESPONSE can hand back the real
    /// sessionId, so the real sessionId cannot be known yet at the point the pipe must be named and started.
    /// </remarks>
    internal sealed class VsControlSessionRegistry : IDisposable
    {
        private readonly ConcurrentDictionary<string, VsControlPipeServer> _servers = new ConcurrentDictionary<string, VsControlPipeServer>();
        private readonly string? _vsControlMcpExecutablePath;

        public VsControlSessionRegistry()
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(ClaudeCodePackage).Assembly.Location);
            var candidate = assemblyDirectory != null
                ? Path.Combine(assemblyDirectory, "ClaudeCode.VsControl.Mcp.exe")
                : null;
            _vsControlMcpExecutablePath = candidate != null && File.Exists(candidate) ? candidate : null;
        }

        /// <summary>False while ClaudeCode.VsControl.Mcp.exe has not been built/deployed next to this assembly yet.</summary>
        public bool IsAvailable => _vsControlMcpExecutablePath != null;

        /// <summary>Starts a new pipe server and returns the MCP server launch spec for it, plus a correlation id for later teardown via <see cref="EndSession"/>.</summary>
        public McpServerConfig StartSession(out string correlationId)
        {
            if (_vsControlMcpExecutablePath == null)
            {
                throw new InvalidOperationException("ClaudeCode.VsControl.Mcp.exe was not found; call IsAvailable first.");
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
}
