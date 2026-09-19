using System;

namespace ClaudeCode.Contracts;

/// <summary>
/// Dependency-free decision logic of the VS control channel's build and Output-window methods
/// (<c>VsControlPipeServer.Build.cs</c> and <c>CountBuildDiagnosticsAsync</c> in the net48 Vsix
/// host). It lives here, beside <see cref="VsDebuggerChannelRules"/>, so the net8.0 suite can
/// exercise the real production logic: a mistake in the project match silently reports a failed
/// build as having no errors, and a mistake in the tail arithmetic silently returns the wrong
/// slice of a build log.
/// </summary>
public static class VsBuildChannelRules
{
    private static readonly char[] _directorySeparators = { '\\', '/' };
    // EnvDTE.OutputWindowPane.Guid for the built-in panes (VSConstants.OutputWindowPaneGuid.*).
    private static readonly Guid _buildOutputPaneGuid = new Guid("1BD8A850-02D1-11D1-BEE7-00A0C913D1F8");
    private static readonly Guid _debugOutputPaneGuid = new Guid("FC076020-078A-11D1-A7DF-00A0C9110051");
    private static readonly Guid _generalOutputPaneGuid = new Guid("3C24D581-5591-4884-A571-9FE89915CD64");

    /// <summary>
    /// Whether an Error List item belongs to the project a <c>buildProject</c> request named.
    /// <c>ErrorItem.Project</c> carries the project's unique name, which is the plain project name
    /// for some project systems and a solution-relative project file path for others, so both
    /// forms have to match. Only an extension ending in <c>proj</c> is stripped: a bare unique
    /// name is routinely dotted (<c>ClaudeCode.Core.Tests</c>), and stripping its last segment
    /// unconditionally would match it against a request for <c>ClaudeCode.Core</c>.
    /// <para>
    /// Both directions of a mistake here are silent. Under-matching skips the row, so a failed
    /// build is reported as <c>succeeded: false</c> with <c>errorCount: 0</c> - a failure with no
    /// errors to act on. Over-matching attributes a sibling project's errors to this build.
    /// </para>
    /// </summary>
    public static bool MatchesProject(string? itemProject, string projectName)
    {
        if (string.IsNullOrEmpty(itemProject))
        {
            return false;
        }

        // A unique name is either bare or solution-relative; only the latter has a directory part.
        // The separators are scanned explicitly rather than via Path: these are always Windows
        // paths produced by Visual Studio, but this assembly is also loaded by the net8.0 test host,
        // where Path would stop treating '\' as a separator.
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
    /// Bounds the agent-supplied <c>maxChars</c> of a <c>getOutput</c> request. The value decides
    /// how much of an Output pane crosses the COM boundary into a managed string on the UI thread,
    /// so it is clamped to the host's ceiling; the floor is 1, not 0, because a request for 0 (or
    /// a negative value) would otherwise ask for a zero-length tail and report the whole pane as
    /// truncated.
    /// </summary>
    public static int ClampOutputChars(int? requestedMaxChars, int defaultChars, int maxChars)
    {
        return Math.Min(maxChars, Math.Max(1, requestedMaxChars ?? defaultChars));
    }

    /// <summary>
    /// The GUID behind one of the three Output pane aliases <c>getOutput</c> documents (Build, Debug,
    /// General), or null for any other pane. Visual Studio's built-in pane names are localized UI
    /// resources, so on a non-English install the documented <c>pane: "Build"</c> and the default
    /// Debug pane match no <c>OutputWindowPane.Name</c>; the GUIDs are fixed across languages and
    /// versions. Every other pane - Git, an extension's log - is still matched by name.
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
    /// Returns the last <paramref name="maxChars"/> characters of Output pane text. The caller
    /// positions an <c>EditPoint</c> with <c>CharLeft</c>, which counts a line break as one
    /// character, while the text it then returns spells that break as two ("\r\n"); the returned
    /// slice can therefore overshoot the cap and has to be trimmed again here. The caller detects
    /// that second trim by comparing lengths, so an over-long pane is still reported as truncated.
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
