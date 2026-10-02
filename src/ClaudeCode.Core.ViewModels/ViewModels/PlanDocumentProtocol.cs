using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Host-side rules for the plan-document WebView2 page.
/// </summary>
public static class PlanDocumentProtocol
{
    /// <summary>Virtual host name mapped to the transcript/plan asset folder.</summary>
    public const string VirtualHostName = "claudecode.plan";

    /// <summary>The one document the plan frame is ever allowed to show.</summary>
    public const string PageUrl = "https://" + VirtualHostName + "/plan.html";

    /// <summary>
    /// True only for the plan page's own document (<see cref="PageUrl"/>), compared path-exact.
    /// </summary>
    public static bool IsPlanDocumentUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri parsed) &&
        string.Equals(parsed.GetLeftPart(UriPartial.Path), PageUrl, StringComparison.Ordinal);
}
