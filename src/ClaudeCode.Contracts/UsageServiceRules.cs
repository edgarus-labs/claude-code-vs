using System;

namespace ClaudeCode.Contracts;

/// <summary>Dependency-free decisions of the VSIX usage service, kept out of the process-spawning
/// glue so they can be unit-tested.</summary>
public static class UsageServiceRules
{
    /// <summary>How long a fetched snapshot answers further requests without re-running the helper.
    /// Only long enough to absorb a burst - the turn-end refresh and the user opening the usage
    /// panel a moment later - so every explicit trigger still sees post-turn numbers.</summary>
    public static readonly TimeSpan RefetchBurstWindow = TimeSpan.FromSeconds(5);

    /// <summary>Whether a snapshot fetched at <paramref name="fetchedAt"/> may still be handed to a
    /// request made at <paramref name="now"/>. A snapshot stamped after <paramref name="now"/> (the
    /// wall clock stepped backwards) is stale: trusting it would otherwise last until the clock
    /// caught up, not for the window.</summary>
    public static bool IsWithinBurstWindow(DateTimeOffset fetchedAt, DateTimeOffset now)
    {
        TimeSpan age = now - fetchedAt;
        return age >= TimeSpan.Zero && age < RefetchBurstWindow;
    }

    /// <summary>Clamps a limit's reported percentage to the 0-100 range the display expects; the
    /// "% used" text prints the value verbatim, so an out-of-range value from the endpoint would
    /// otherwise reach the user unchanged.</summary>
    public static int ClampPercent(int percent)
    {
        return Math.Max(0, Math.Min(100, percent));
    }
}
