using System.Collections.Generic;

namespace ClaudeCode.Contracts;

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
