using System;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeCode.Acp;

public sealed class AcpRemoteException : Exception
{
    public AcpRemoteException(int code, string message, JsonNode? data = null)
        : base(FormatMessage(message, data))
    {
        Code = code;
        RemoteData = data;
    }

    public int Code { get; }

    /// <summary>
    /// The raw, UNREDACTED <c>error.data</c> payload the remote agent sent. Unlike <see cref="Exception.Message"/>
    /// (already redacted via <see cref="FormatMessage"/>), this can contain stderr, prompts, request
    /// bodies, or credentials. Never log or display it directly - inspect specific known-safe fields only.
    /// </summary>
    public JsonNode? RemoteData { get; }

    private static string FormatMessage(string message, JsonNode? data)
    {
        string summary = Redact(message, 256);
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "The ACP agent reported an error.";
        }

        // Never stringify error.data: it can contain stderr, prompts, request bodies, or credentials.
        if (data is not JsonObject details)
        {
            return summary;
        }

        string? kind = ReadString(details, "errorKind");
        if (IsKnownErrorKind(kind))
        {
            summary += " [" + kind + "]";
        }

        string? detail = ReadString(details, "details");
        if (!string.IsNullOrWhiteSpace(detail))
        {
            string safeDetail = Redact(detail!, 512);
            if (safeDetail.Length > 0 && !string.Equals(safeDetail, summary, StringComparison.Ordinal))
            {
                summary += ": " + safeDetail;
            }
        }

        return summary;
    }

    private static string? ReadString(JsonObject data, string key) =>
        data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsKnownErrorKind(string? kind) => kind switch
    {
        "authentication_failed" or "oauth_org_not_allowed" or "account_on_hold" or
        "verification_required" or "billing_error" or "rate_limit" or "overloaded" or
        "invalid_request" or "model_not_found" or "server_error" or "unknown" or
        "max_output_tokens" or "cloud_credential_error" or "transport_lost" or
        "worker_shutdown" or "no_result" => true,
        _ => false,
    };

    private static readonly char[] _lineBreakChars = { '\r', '\n' };
    private static readonly char[] _jsonStartChars = { '{', '[' };

    private static string Redact(string text, int limit)
    {
        // Bound work before applying expressions, and hide all later lines (often SDK stderr/stack).
        int end = text.IndexOfAny(_lineBreakChars);
        string safe = text.Substring(0, Math.Min(end < 0 ? text.Length : end, 4096));
        safe = Regex.Replace(safe, @"\p{C}", "");
        int payload = safe.IndexOfAny(_jsonStartChars);
        if (payload >= 0)
        {
            safe = safe.Substring(0, payload) + " (structured data omitted)";
        }
        safe = Regex.Replace(safe,
            @"(?i)\b(?:authorization|proxy-authorization|cookie|set-cookie|(?:[a-z][a-z0-9]*[_-])*(?:token|key|secret|password)|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|client[_ -]?secret)\b[""']?(?:\s*[:=]\s*|\s+)(?:""[^""]*""|'[^']*'|[^,;]+)",
            "[redacted]");
        safe = Regex.Replace(safe, @"(?i)\b(?:bearer|basic)\s+\S+", "[redacted]");
        safe = Regex.Replace(safe, @"(?i)\b(?:https?|file)://\S+", "[redacted URL]");
        safe = Regex.Replace(safe, @"(?i)\b(?:sk-[a-z0-9_-]+|gh[pousr]_[a-z0-9_]+|github_pat_[a-z0-9_]+|eyJ[a-z0-9_.=-]+)\b", "[redacted]");
        safe = Regex.Replace(safe, @"[a-zA-Z0-9_+/=-]{32,}", "[redacted]");
        safe = safe.Trim();
        return safe.Length <= limit ? safe : safe.Substring(0, limit) + "…";
    }
}
