using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.VsControl.Mcp;

/// <summary>
/// Provides the console application entry point, which parses the pipe argument from the command line and starts processing.
/// </summary>
public static class Program
{
    /// <summary>
    /// The pipe argument name.
    /// </summary>
    private const string _pipeArgumentName = "--pipe";

    /// <summary>
    /// Asynchronously runs the Visual Studio control MCP server over standard input and output, connecting it to the specified named pipe and returning 0 on normal completion or 1 when the required pipe argument is missing or invalid.
    /// </summary>
    /// <param name="args">The collection of args.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the int.</returns>
    public static async Task<int> Main(string[] args)
    {
        string? pipeName = ParsePipeArgument(args);
        if (string.IsNullOrEmpty(pipeName))
        {
            await Console.Error.WriteLineAsync(
                $"ClaudeCode.VsControl.Mcp: missing required '{_pipeArgumentName} <name>' argument.").ConfigureAwait(false);

            return 1;
        }

        VsControlPipeClient pipeClient;
        try
        {
            pipeClient = new VsControlPipeClient(pipeName);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync($"ClaudeCode.VsControl.Mcp: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        await using var ownedPipeClient = pipeClient;

        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellationSource.Cancel();
        };

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8NoBom);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8NoBom) { AutoFlush = false, NewLine = "\n" };

        using var server = new McpServer(pipeClient, input, output);
        await server.RunAsync(cancellationSource.Token).ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Parses the command-line arguments to extract the value of the pipe argument, supporting both the &quot;--pipe value&quot; and &quot;--pipe=value&quot; forms, and returns null if the argument is absent or has no following value.
    /// </summary>
    /// <param name="args">The collection of args.</param>
    /// <returns>The string? result.</returns>
    public static string? ParsePipeArgument(string[] args)
    {
        const string equalsPrefix = _pipeArgumentName + "=";

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, _pipeArgumentName, StringComparison.Ordinal))
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
