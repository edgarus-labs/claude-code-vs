using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Represents the configuration settings for an MCP server, including its name, executable command, command-line arguments, and environment variables.
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

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the command.
    /// </summary>
    public string Command { get; }

    /// <summary>
    /// Gets the collection of args.
    /// </summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>
    /// Gets the env.
    /// </summary>
    public IReadOnlyDictionary<string, string> Env { get; }
}
