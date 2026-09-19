using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Host-side rules for the plan-document WebView2 page: which URL is the one document the plan frame
/// is ever allowed to show. The plan and transcript pages are served from the same asset folder on
/// different virtual hosts, so a frame navigated to a sibling path (e.g. <c>index.html</c>) cannot be
/// driven by the host's <c>window.claudePlan</c> script and would render stale content under a live
/// Proceed/Review header. Kept free of WPF/WebView2 types so the security-relevant decision is
/// unit-testable; <c>ClaudeCode.Core.Views.PlanDocumentView</c> owns only the CoreWebView2 plumbing
/// that feeds it.
/// </summary>
public static class PlanDocumentProtocol
{
    /// <summary>Virtual host name mapped to the transcript/plan asset folder.</summary>
    public const string VirtualHostName = "claudecode.plan";

    /// <summary>The one document the plan frame is ever allowed to show.</summary>
    public const string PageUrl = "https://" + VirtualHostName + "/plan.html";

    /// <summary>
    /// True only for the plan page's own document, compared path-exact. A prefix match would accept
    /// <c>https://claudecode.plan/plan.html/../index.html</c> (and any longer sibling path), letting
    /// the frame be navigated to a document this view does not drive; <see cref="Uri"/> does not
    /// collapse the <c>..</c> segments, so the exact-path comparison still rejects them.
    /// </summary>
    public static bool IsPlanDocumentUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri parsed) &&
        string.Equals(parsed.GetLeftPart(UriPartial.Path), PageUrl, StringComparison.Ordinal);
}
