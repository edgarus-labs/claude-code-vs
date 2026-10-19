using System.Collections.Generic;
using System.Text;

namespace ClaudeCode.Acp;

/// <summary>
/// Provides helper methods for escaping and assembling command-line arguments passed to external processes.
/// </summary>
public static class ProcessArgumentEscaping
{
    /// <summary>
    /// Converts a sequence of command-line arguments into a single properly escaped and space-delimited arguments string.
    /// </summary>
    /// <param name="arguments">The collection of arguments.</param>
    /// <returns>The string result.</returns>
    public static string ToArgumentsString(IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        foreach (string argument in arguments)
        {
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends a command-line argument to the specified StringBuilder, separating it from any existing content with a space and escaping quotes and backslashes per Windows command-line parsing rules, wrapping the argument in double quotes when it is empty or contains whitespace or quotes.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="argument">The argument.</param>
    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (builder.Length != 0)
        {
            builder.Append(' ');
        }

        if (argument.Length != 0 && ContainsNoWhitespaceOrQuotes(argument))
        {
            builder.Append(argument);

            return;
        }

        builder.Append('"');
        int index = 0;
        while (index < argument.Length)
        {
            char c = argument[index++];
            if (c == '\\')
            {
                int backslashCount = 1;
                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashCount++;
                }

                if (index == argument.Length)
                {
                    builder.Append('\\', backslashCount * 2);
                }
                else if (argument[index] == '"')
                {
                    builder.Append('\\', backslashCount * 2 + 1).Append('"');
                    index++;
                }
                else
                {
                    builder.Append('\\', backslashCount);
                }
            }
            else if (c == '"')
            {
                builder.Append('\\').Append('"');
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Determines whether the specified string is free of whitespace characters and double-quote characters, returning false if either is found.
    /// </summary>
    /// <param name="s">The s.</param>
    /// <returns>true if the operation succeeded; otherwise, false.</returns>
    private static bool ContainsNoWhitespaceOrQuotes(string s)
    {
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || c == '"')
            {
                return false;
            }
        }

        return true;
    }
}
