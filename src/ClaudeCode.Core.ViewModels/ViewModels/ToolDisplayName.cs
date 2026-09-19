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
    /// A denial-of-service bound on the returned string, not a layout rule: the permission card
    /// bounds its own height with a ScrollViewer (ChatPanelView.xaml) and the transcript header
    /// clips with an ellipsis, so the text itself may keep every line the agent authored. The
    /// single-line rule for the attention notification lives in
    /// <see cref="SessionTitleFormat.SingleLine"/>, applied by <c>ChatViewModel.RaiseAttention</c>.
    /// </summary>
    public const int MaxDisplayLength = 4_000;

    /// <summary>
    /// Formats <paramref name="title"/> for display. MCP identifiers become
    /// "<c>Visual Studio: List app windows</c>"; anything else is already human-authored
    /// (<c>Read</c>, <c>Bash</c>, <c>Find "**/*.sln"</c>) and keeps its wording, line breaks
    /// included. A Bash/PowerShell title is the whole command and, with no terminal capability
    /// advertised, the only text the permission card can show for it - the user cannot approve a
    /// tail they were not shown, so lines are never collapsed or cut short of
    /// <see cref="MaxDisplayLength"/>. Control and bidi characters are removed on every line.
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

        // A segment built only from separators ("mcp__.__editDocument") has no words to label, so it
        // joins the malformed branches above and keeps its routing identifier. Dropping an empty
        // server label instead would render an MCP call as a built-in - "Edit document" reaches the
        // transcript as a first-party file edit - losing the one invariant this function exists to
        // establish; an empty tool label would leave a subjectless "Visual Studio: ".
        if (serverLabel.Length == 0 || toolLabel.Length == 0)
        {
            return Cap(value);
        }

        return Cap($"{serverLabel}: {toolLabel}");
    }

    /// <summary>
    /// Drops control and bidi formatting characters, which can hide or reverse part of the name the
    /// user is approving, line by line so the line structure of a shell command survives. A tab
    /// becomes a space rather than vanishing, so it still separates the words it separated. Runs
    /// before the MCP marker is matched, so a leading control character cannot conceal it.
    /// </summary>
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

        // Cutting between the halves of a surrogate pair would leave a lone high surrogate that
        // renders as a replacement box before the ellipsis.
        int cut = MaxDisplayLength - 1;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value.Substring(0, cut).TrimEnd() + "…";
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
