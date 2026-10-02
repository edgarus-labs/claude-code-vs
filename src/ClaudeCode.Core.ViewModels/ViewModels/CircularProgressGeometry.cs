using System;
using System.Globalization;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Geometry and parameter parsing for circular context/usage rings.
/// </summary>
public static class CircularProgressGeometry
{
    public const double DefaultRadius = 6;
    public const double DefaultThickness = 2;

    /// <summary>
    /// Parses a "radius,thickness" specification string. Falls back to positive defaults when it is
    /// missing, malformed, or contains non-positive numbers.
    /// </summary>
    public static (double Radius, double Thickness) ParseSpec(string? spec, double defaultRadius = DefaultRadius, double defaultThickness = DefaultThickness)
    {
        double radius = defaultRadius > 0 ? defaultRadius : DefaultRadius;
        double thickness = defaultThickness > 0 ? defaultThickness : DefaultThickness;

        if (spec is not null)
        {
            var parts = spec.Split(',');
            if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedRadius) && parsedRadius > 0)
            {
                radius = parsedRadius;
            }

            if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedThickness) && parsedThickness > 0)
            {
                thickness = parsedThickness;
            }
        }

        return (radius, thickness);
    }

    /// <summary>
    /// Computes the stroke dash and gap for a 0–100 percentage. Guarantees finite, non-negative numbers
    /// without NaN or Infinity.
    /// </summary>
    public static (double Dash, double Gap) ComputeDash(int percent, double radius, double thickness)
    {
        if (radius <= 0) radius = DefaultRadius;
        if (thickness <= 0) thickness = DefaultThickness;

        double clamped = Math.Max(0, Math.Min(100, percent));
        double circumference = 2 * Math.PI * radius / thickness;
        return (circumference * clamped / 100, circumference + 1);
    }
}
