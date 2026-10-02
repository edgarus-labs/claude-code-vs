using System;
using System.Collections.Generic;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Formats agent-reported tool titles for display, turning MCP routing identifiers such as
/// <c>mcp__visual-studio__listAppWindows</c> into readable text.
/// </summary>
public static class ToolDisplayName
{
    private const string McpPrefix = "mcp__";
    private const string McpSeparator = "__";

    /// <summary>
    /// Maximum length of a string returned by <see cref="Describe"/>.
    /// </summary>
    public const int MaxDisplayLength = 4_000;

    /// <summary>
    /// Formats <paramref name="title"/> for display. MCP identifiers become
    /// "<c>Visual Studio: List app windows</c>"; any other title keeps its wording, line breaks
    /// included, up to <see cref="MaxDisplayLength"/>. Control and bidi characters are removed on every line.
    /// </summary>
    public static string Describe(string? title)
    {
        string value = Normalize(title);
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!value.StartsWith(McpPrefix, StringComparison.Ordinal))
        {
            return Cap(value);
        }

        int separator = value.IndexOf(McpSeparator, McpPrefix.Length, StringComparison.Ordinal);
        if (separator < 0)
        {
            return Cap(value);
        }

        string server = value.Substring(McpPrefix.Length, separator - McpPrefix.Length);
        string tool = value.Substring(separator + McpSeparator.Length);
        if (server.Length == 0 || tool.Length == 0)
        {
            return Cap(value);
        }

        string serverLabel = TitleCase(server);
        string toolLabel = SentenceCase(tool);

        if (serverLabel.Length == 0 || toolLabel.Length == 0)
        {
            return Cap(value);
        }

        return Cap($"{serverLabel}: {toolLabel}");
    }

    private static string Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        string[] lines = title!.Replace('\t', ' ').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = SessionTitleFormat.StripControlAndBidi(lines[i]).TrimEnd();
        }

        return string.Join("\n", lines).Trim();
    }

    private static string Cap(string value)
    {
        if (value.Length <= MaxDisplayLength)
        {
            return value;
        }

        int cut = MaxDisplayLength - 1;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value.Substring(0, cut).TrimEnd() + "…";
    }

    private static string TitleCase(string slug)
    {
        var builder = new StringBuilder(slug.Length);
        foreach (string word in Words(slug))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(IsAcronym(word) ? word : Capitalize(word));
        }

        return builder.ToString();
    }

    private static string SentenceCase(string identifier)
    {
        var builder = new StringBuilder(identifier.Length + 4);
        foreach (string word in Words(identifier))
        {
            if (builder.Length == 0)
            {
                builder.Append(IsAcronym(word) ? word : Capitalize(word));
                continue;
            }

            builder.Append(' ');
            builder.Append(IsAcronym(word) ? word : word.ToLowerInvariant());
        }

        return builder.ToString();
    }

    private static IEnumerable<string> Words(string value)
    {
        var current = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '-' || c == '_' || c == '.' || c == ' ')
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            bool startsHump = char.IsUpper(c)
                && current.Length > 0
                && (!char.IsUpper(current[current.Length - 1])
                    || (i + 1 < value.Length && char.IsLower(value[i + 1])));

            if (startsHump)
            {
                yield return current.ToString();
                current.Clear();
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static bool IsAcronym(string word) =>
        word.Length > 1 && IsAllUpper(word);

    private static bool IsAllUpper(string word)
    {
        foreach (char c in word)
        {
            if (char.IsLower(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word.Substring(1);
}
