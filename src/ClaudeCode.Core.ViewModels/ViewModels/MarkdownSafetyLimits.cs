using System;
using System.Net;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Length and link guardrails for untrusted assistant Markdown rendered in the transcript and plan
/// document.
/// </summary>
public static class MarkdownSafetyLimits
{
    public const int MaxMarkdownLength = 200_000;
    internal const string TruncationNotice = "\n\n*(message truncated: exceeded the maximum renderable size)*";

    /// <summary>
    /// Truncates markdown text to at most <paramref name="maxLength"/> characters plus a truncation notice.
    /// </summary>
    public static string LimitMarkdownLength(string markdown, int maxLength = MaxMarkdownLength)
    {
        if (markdown.Length <= maxLength)
        {
            return markdown;
        }

        return markdown.Substring(0, maxLength) + TruncationNotice;
    }

    /// <summary>
    /// True only for absolute http/https links whose host is neither loopback nor an unspecified
    /// IP address. IPv4-mapped IPv6 addresses are checked as IPv4 destinations.
    /// </summary>
    public static bool IsNavigableLink(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrEmpty(uri.Host) || uri.IsLoopback)
        {
            return false;
        }

        if (!IPAddress.TryParse(uri.DnsSafeHost, out var address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return !IPAddress.IsLoopback(address) &&
            !address.Equals(IPAddress.Any) &&
            !address.Equals(IPAddress.IPv6Any);
    }
}
