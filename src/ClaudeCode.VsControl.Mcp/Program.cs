using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.VsControl.Mcp;

public static class Program
{
    private const string _pipeArgumentName = "--pipe";

    public static async Task<int> Main(string[] args)
    {
        string? pipeName = ParsePipeArgument(args);
        if (string.IsNullOrEmpty(pipeName))
        {
            await Console.Error.WriteLineAsync(
                $"ClaudeCode.VsControl.Mcp: missing required '{_pipeArgumentName} <name>' argument.").ConfigureAwait(false);

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

        using var server = new McpServer(pipeClient, input, output);
        await server.RunAsync(cancellationSource.Token).ConfigureAwait(false);

        return 0;
    }

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
