using System.Collections.Generic;
using System.Text;

namespace ClaudeCode.Acp;

public static class ProcessArgumentEscaping
{
    public static string ToArgumentsString(IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        foreach (string argument in arguments)
        {
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

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
