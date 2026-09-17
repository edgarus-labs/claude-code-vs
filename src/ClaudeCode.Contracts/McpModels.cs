using System.Collections.Generic;

namespace ClaudeCode.Contracts
{
    /// <summary>
    /// Launch spec for a client-side MCP (Model Context Protocol) server injected into an ACP `session/new`
    /// call, so the agent can call it as an ordinary tool during the conversation.
    /// </summary>
    public sealed class McpServerConfig
    {
        public McpServerConfig(string name, string command, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null)
        {
            Name = name;
            Command = command;
            Args = args;
            Env = env ?? new Dictionary<string, string>();
        }

        public string Name { get; }
        public string Command { get; }
        public IReadOnlyList<string> Args { get; }
        public IReadOnlyDictionary<string, string> Env { get; }
    }

    /// <summary>
    /// NDJSON request/response envelope used on the private named-pipe channel between the in-process
    /// "visual-studio" MCP server (ClaudeCode.VsControl.Mcp, spawned per session) and the running VS
    /// extension (ClaudeCode.Vsix), which alone can touch DTE/IVsSolution/the editor on the UI thread.
    /// Method names and per-method JSON param/result shapes are documented in VsControlProtocol.md.
    /// </summary>
    public sealed class VsControlRequest
    {
        public string Id { get; set; } = "";
        public string Method { get; set; } = "";
        public string ParamsJson { get; set; } = "{}";
    }

    public sealed class VsControlResponse
    {
        public string Id { get; set; } = "";
        public string? ResultJson { get; set; }
        public string? Error { get; set; }
    }
}
