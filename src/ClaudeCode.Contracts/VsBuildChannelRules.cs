using System;

namespace ClaudeCode.Contracts;

/// <summary>
/// Dependency-free decision logic of the VS control channel's build and Output-window methods.
/// </summary>
public static class VsBuildChannelRules
{
    private static readonly char[] _directorySeparators = { '\\', '/' };
    private static readonly Guid _buildOutputPaneGuid = new Guid("1BD8A850-02D1-11D1-BEE7-00A0C913D1F8");
    private static readonly Guid _debugOutputPaneGuid = new Guid("FC076020-078A-11D1-A7DF-00A0C9110051");
    private static readonly Guid _generalOutputPaneGuid = new Guid("3C24D581-5591-4884-A571-9FE89915CD64");

    /// <summary>
    /// Whether an Error List item belongs to the project a <c>buildProject</c> request named.
    /// Matches <paramref name="itemProject"/> case-insensitively against
    /// <paramref name="projectName"/> either as a plain project name or as a project file path whose
    /// file name, with an extension ending in <c>proj</c> stripped, equals it. Returns false for a
    /// null or empty <paramref name="itemProject"/>.
    /// </summary>
    public static bool MatchesProject(string? itemProject, string projectName)
    {
        if (string.IsNullOrEmpty(itemProject))
        {
            return false;
        }

        var start = itemProject!.LastIndexOfAny(_directorySeparators) + 1;
        var candidate = start == 0 ? itemProject! : itemProject!.Substring(start);
        if (string.Equals(candidate, projectName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dot = candidate.LastIndexOf('.');
        if (dot < 0 || !candidate.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(candidate.Substring(0, dot), projectName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bounds the agent-supplied <c>maxChars</c> of a <c>getOutput</c> request: uses
    /// <paramref name="defaultChars"/> when none is given and clamps the result to the range 1 to
    /// <paramref name="maxChars"/>.
    /// </summary>
    public static int ClampOutputChars(int? requestedMaxChars, int defaultChars, int maxChars)
    {
        return Math.Min(maxChars, Math.Max(1, requestedMaxChars ?? defaultChars));
    }

    /// <summary>
    /// The language-independent GUID of the Build, Debug or General Output pane, matched
    /// case-insensitively by name, or null for any other pane.
    /// </summary>
    public static Guid? WellKnownOutputPaneGuid(string paneName)
    {
        if (string.Equals(paneName, "Build", StringComparison.OrdinalIgnoreCase))
        {
            return _buildOutputPaneGuid;
        }

        if (string.Equals(paneName, "Debug", StringComparison.OrdinalIgnoreCase))
        {
            return _debugOutputPaneGuid;
        }

        if (string.Equals(paneName, "General", StringComparison.OrdinalIgnoreCase))
        {
            return _generalOutputPaneGuid;
        }

        return null;
    }

    /// <summary>
    /// Returns the last <paramref name="maxChars"/> characters of Output pane text, or an empty
    /// string when <paramref name="text"/> is null or empty or <paramref name="maxChars"/> is not
    /// positive.
    /// </summary>
    public static string TakeOutputTail(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
        {
            return string.Empty;
        }

        return text!.Length <= maxChars ? text! : text!.Substring(text!.Length - maxChars);
    }
}
