using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.Acp;

/// <summary>
/// Maps a resolved ACP adapter invocation onto a host-supplied launcher script that wraps the stock adapter.
/// </summary>
public static class AcpLauncherWrap
{
    private const string PackageEntrySuffix = "/@agentclientprotocol/claude-agent-acp/dist/index.js";

    /// <summary>
    /// When <paramref name="resolved"/> is the `node &lt;pkg&gt;/dist/index.js` shape and the launcher exists,
    /// runs <paramref name="launcherPath"/> instead and points it at the adapter package through
    /// <c>CLAUDE_ACP_ADAPTER_DIR</c>. Otherwise returns the invocation unchanged.
    /// </summary>
    public static (string FileName, IReadOnlyList<string>? Arguments, IReadOnlyDictionary<string, string>? Environment) Wrap(
        AcpExecutableSpec resolved,
        string? launcherPath)
    {
        if (resolved is null)
        {
            throw new ArgumentNullException(nameof(resolved));
        }

        var entry = resolved.Arguments is { Count: 1 } ? resolved.Arguments[0] : null;
        var normalized = entry?.Replace('\\', '/');
        if (normalized is null || !normalized.EndsWith(PackageEntrySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return (resolved.FileName, resolved.Arguments, null);
        }

        if (string.IsNullOrEmpty(launcherPath) || !File.Exists(launcherPath))
        {
            return (resolved.FileName, resolved.Arguments, null);
        }

        var packageDirectory = Path.GetDirectoryName(Path.GetDirectoryName(entry!));
        if (string.IsNullOrEmpty(packageDirectory))
        {
            return (resolved.FileName, resolved.Arguments, null);
        }

        if (!File.Exists(Path.Combine(packageDirectory!, "dist", "acp-agent.js")))
        {
            return (resolved.FileName, resolved.Arguments, null);
        }

        return (
            resolved.FileName,
            new[] { launcherPath! },
            new Dictionary<string, string> { ["CLAUDE_ACP_ADAPTER_DIR"] = packageDirectory! });
    }
}
