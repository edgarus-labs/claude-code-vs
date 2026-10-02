using System;

namespace ClaudeCode.Contracts;

/// <summary>Dependency-free decisions of the VSIX usage service.</summary>
public static class UsageServiceRules
{
    /// <summary>How long a fetched snapshot answers further requests without re-running the
    /// helper.</summary>
    public static readonly TimeSpan RefetchBurstWindow = TimeSpan.FromSeconds(5);

    /// <summary>Whether a snapshot fetched at <paramref name="fetchedAt"/> may still be handed to a
    /// request made at <paramref name="now"/>. A snapshot stamped after <paramref name="now"/> is
    /// stale.</summary>
    public static bool IsWithinBurstWindow(DateTimeOffset fetchedAt, DateTimeOffset now)
    {
        TimeSpan age = now - fetchedAt;
        return age >= TimeSpan.Zero && age < RefetchBurstWindow;
    }

    /// <summary>Clamps a limit's reported percentage to the 0-100 range.</summary>
    public static int ClampPercent(int percent)
    {
        return Math.Max(0, Math.Min(100, percent));
    }
}
