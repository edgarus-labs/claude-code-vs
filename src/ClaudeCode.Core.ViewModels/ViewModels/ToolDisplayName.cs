using System;
using System.Collections.Generic;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Turns a tool title into something a human can act on. Tool titles cross the wire from the agent,
/// and MCP servers are addressed by a routing identifier rather than a label: a Visual Studio tool
/// arrives as <c>mcp__visual-studio__listAppWindows</c>. That string is what the permission prompt
/// asks the user to approve, so it has to read as a sentence - the user is deciding whether to let
/// the agent do something, and cannot do that from a routing key.
/// </summary>
public static class ToolDisplayName
{
    private const string McpPrefix = "mcp__";
    private const string McpSeparator = "__";

    /// <summary>
    /// Formats <paramref name="title"/> for display. MCP identifiers become
    /// "<c>Visual Studio: List app windows</c>"; anything else is already human-authored
    /// (<c>Read</c>, <c>Bash</c>, <c>Find "**/*.sln"</c>) and is returned untouched.
    /// </summary>
    public static string Describe(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string value = title!.Trim();
        if (!value.StartsWith(McpPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        int separator = value.IndexOf(McpSeparator, McpPrefix.Length, StringComparison.Ordinal);
        if (separator < 0)
        {
            return value;
        }

        string server = value.Substring(McpPrefix.Length, separator - McpPrefix.Length);
        string tool = value.Substring(separator + McpSeparator.Length);
        if (server.Length == 0 || tool.Length == 0)
        {
            return value;
        }

        string serverLabel = TitleCase(server);
        string toolLabel = SentenceCase(tool);
        return serverLabel.Length == 0 ? toolLabel : $"{serverLabel}: {toolLabel}";
    }

    /// <summary>"visual-studio" -> "Visual Studio".</summary>
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

    /// <summary>"listAppWindows" -> "List app windows", preserving acronyms like "UI".</summary>
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

    /// <summary>Splits on separators and camel/Pascal humps, keeping uppercase runs together.</summary>
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

            // A hump starts at an uppercase letter that follows a lowercase/digit, or that is the
            // last uppercase of a run ("UIWindow" -> "UI", "Window").
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
