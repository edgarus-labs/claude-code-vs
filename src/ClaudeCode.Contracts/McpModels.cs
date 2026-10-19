using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Represents the configuration for an MCP server, including its name, command, arguments, and environment variables.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>
    /// Initializes a new instance of the McpServerConfig class with the specified name, command, arguments, and optional environment variables.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="command">The command containing the operation data.</param>
    /// <param name="args">The collection of args.</param>
    /// <param name="env">The env.</param>
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
