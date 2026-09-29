using System.Collections.Generic;

namespace ClaudeCode.Core.Effort;

/// <summary>
/// Turns the head's Low/Medium/High probabilities into the effort a turn gets. Deliberately
/// asymmetric: too much effort only costs time and tokens, too little can cost the answer. So Low
/// needs a clear verdict, High needs a majority, and everything in between - including a model that
/// cannot tell - is Medium.
/// </summary>
public static class AutoEffortPolicy
{
    /// <summary>Minimum P(Low) to go below Medium.</summary>
    public const float LowThreshold = 0.70f;

    /// <summary>Minimum P(High) to go above Medium.</summary>
    public const float HighThreshold = 0.50f;

    /// <param name="probabilities">P(Low), P(Medium), P(High), in <see cref="EffortLevel"/> order.</param>
    public static EffortLevel Decide(IReadOnlyList<float> probabilities)
    {
        float low = probabilities[(int)EffortLevel.Low];
        float high = probabilities[(int)EffortLevel.High];
        // NaN compares false everywhere, so a corrupt output lands on Medium by construction;
        // infinity is rejected explicitly.
        if (float.IsInfinity(low) || float.IsInfinity(high)) return EffortLevel.Medium;
        if (high >= HighThreshold) return EffortLevel.High;
        if (low >= LowThreshold) return EffortLevel.Low;
        return EffortLevel.Medium;
    }
}
