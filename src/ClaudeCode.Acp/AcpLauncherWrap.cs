using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.Acp;

/// <summary>
/// Maps a resolved ACP adapter invocation onto a host-supplied launcher script that wraps the stock
/// adapter (adding host-specific extension requests) without changing what the adapter itself does.
/// Pure string/file-probe logic so the host only has to supply the launcher path.
/// </summary>
public static class AcpLauncherWrap
{
    private const string PackageEntrySuffix = "/@agentclientprotocol/claude-agent-acp/dist/index.js";

    /// <summary>
    /// When <paramref name="resolved"/> is the `node &lt;pkg&gt;/dist/index.js` shape, runs
    /// <paramref name="launcherPath"/> instead and points it at the adapter package through
    /// <c>CLAUDE_ACP_ADAPTER_DIR</c>. Any other executable shape (a custom wrapper, a native build),
    /// an absent launcher, or an adapter whose internal entry point has moved is returned unchanged,
    /// so the session still starts - it just has no launcher-provided extensions.
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

        // The launcher imports the adapter's *internal* dist/acp-agent.js, which the package does
        // not publish as an entry point. The adapter is installed and upgraded by the user, so a
        // release that moves or renames that module would otherwise break every session with
        // ERR_MODULE_NOT_FOUND. Degrade to the stock entry instead.
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
