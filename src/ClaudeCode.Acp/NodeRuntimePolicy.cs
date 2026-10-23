using System;
using System.Globalization;

namespace ClaudeCode.Acp;

/// <summary>
/// Decides whether a Node.js runtime version carries the Brotli decoder fix for CVE-2026-111497.
/// A version that cannot be parsed is never reported as unsupported.
/// </summary>
public static class NodeRuntimePolicy
{
    /// <summary>
    /// The oldest patched release of each affected line; every later major is unaffected.
    /// </summary>
    public const string PatchedReleases = "22.23.4, 24.22.1 or 26.12.1";

    private const int _firstUnaffectedMajor = 27;

    /// <summary>
    /// Parses the output of <c>node --version</c> (<c>v22.23.4</c>, or <c>22.23.4</c>) into a three-part version.
    /// Pre-release and build suffixes are rejected rather than guessed at.
    /// </summary>
    /// <param name="text">The version text.</param>
    /// <param name="version">The parsed version, or 0.0.0 when parsing fails.</param>
    /// <returns>true if the text is a plain major.minor.patch version; otherwise, false.</returns>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed.Substring(1);
        }

        string[] parts = trimmed.Split('.');
        if (parts.Length != 3
            || !TryParsePart(parts[0], out int major)
            || !TryParsePart(parts[1], out int minor)
            || !TryParsePart(parts[2], out int patch))
        {
            return false;
        }

        version = new Version(major, minor, patch);

        return true;
    }

    /// <summary>
    /// Determines whether the version is a patched 22.x, 24.x or 26.x release, or a major from 27 on.
    /// </summary>
    /// <param name="version">The version.</param>
    /// <returns>true if the runtime is supported; otherwise, false.</returns>
    public static bool IsSupported(Version version) => version.Major switch
    {
        22 => version >= new Version(22, 23, 4),
        24 => version >= new Version(24, 22, 1),
        26 => version >= new Version(26, 12, 1),
        _ => version.Major >= _firstUnaffectedMajor,
    };

    /// <summary>
    /// Builds the user-facing warning for an unsupported runtime.
    /// </summary>
    /// <param name="versionText">The output of <c>node --version</c>.</param>
    /// <returns>The warning, or null when the version is supported or cannot be determined.</returns>
    public static string? GetWarning(string? versionText)
    {
        if (!TryParseVersion(versionText, out Version version) || IsSupported(version))
        {
            return null;
        }

        return "Node.js " + version.ToString(3) + " is affected by the Brotli decoder vulnerability CVE-2026-111497. "
            + "Update Node.js to " + PatchedReleases + " or newer, then restart Visual Studio.";
    }

    private static bool TryParsePart(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
