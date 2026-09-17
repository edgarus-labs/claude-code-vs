using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.VsControl.Mcp;

/// <summary>
/// Entry point for the standalone "visual-studio" MCP server. Spawned once per ACP session by
/// ClaudeCode.Vsix with a <c>--pipe &lt;name&gt;</c> argument identifying the named pipe the running
/// Visual Studio instance is listening on (see docs/VsControlProtocol.md). Speaks MCP (newline-delimited
/// JSON-RPC 2.0, see <see cref="McpServer"/>) on stdio to the agent process, and NDJSON
/// VsControlRequest/VsControlResponse envelopes on the named pipe to the Vsix host. Has no dependency on
/// the VS SDK: it builds and runs identically whether or not a Vsix host is attached to the pipe, and
/// fails cleanly (an MCP tool error, not a crash) when it isn't.
/// </summary>
public static class Program
{
    private const string PipeArgumentName = "--pipe";

    public static async Task<int> Main(string[] args)
    {
        string? pipeName = ParsePipeArgument(args);
        if (string.IsNullOrEmpty(pipeName))
        {
            await Console.Error.WriteLineAsync(
                $"ClaudeCode.VsControl.Mcp: missing required '{PipeArgumentName} <name>' argument.").ConfigureAwait(false);
            return 1;
        }

        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellationSource.Cancel();
        };

        await using var pipeClient = new VsControlPipeClient(pipeName);

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8NoBom);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8NoBom) { AutoFlush = false, NewLine = "\n" };

        var server = new McpServer(pipeClient, input, output);
        await server.RunAsync(cancellationSource.Token).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Extracts the value of <c>--pipe &lt;name&gt;</c> or <c>--pipe=&lt;name&gt;</c> from argv.</summary>
    public static string? ParsePipeArgument(string[] args)
    {
        const string equalsPrefix = PipeArgumentName + "=";

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, PipeArgumentName, StringComparison.Ordinal))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }

            if (arg.StartsWith(equalsPrefix, StringComparison.Ordinal))
            {
                return arg[equalsPrefix.Length..];
            }
        }

        return null;
    }
}
