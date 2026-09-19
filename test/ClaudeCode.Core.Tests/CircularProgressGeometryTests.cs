using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class CircularProgressGeometryTests
{
    [Fact]
    public void ParseSpec_NullOrEmpty_ReturnsDefaults()
    {
        var (radius, thickness) = CircularProgressGeometry.ParseSpec(null);
        Assert.Equal(CircularProgressGeometry.DefaultRadius, radius);
        Assert.Equal(CircularProgressGeometry.DefaultThickness, thickness);

        var (radiusEmpty, thicknessEmpty) = CircularProgressGeometry.ParseSpec(string.Empty);
        Assert.Equal(CircularProgressGeometry.DefaultRadius, radiusEmpty);
        Assert.Equal(CircularProgressGeometry.DefaultThickness, thicknessEmpty);
    }

    [Fact]
    public void ParseSpec_ValidSpec_ReturnsParsedValues()
    {
        var (radius, thickness) = CircularProgressGeometry.ParseSpec("10,3");
        Assert.Equal(10.0, radius);
        Assert.Equal(3.0, thickness);
    }

    [Fact]
    public void ParseSpec_ZeroOrNegativeThickness_RetainsDefaultPositiveThickness()
    {
        // Parameter "6,0" or "6,-2" must never set thickness to <= 0.
        var (radiusZero, thicknessZero) = CircularProgressGeometry.ParseSpec("6,0");
        Assert.Equal(6.0, radiusZero);
        Assert.Equal(CircularProgressGeometry.DefaultThickness, thicknessZero);

        var (radiusNeg, thicknessNeg) = CircularProgressGeometry.ParseSpec("6,-2");
        Assert.Equal(6.0, radiusNeg);
        Assert.Equal(CircularProgressGeometry.DefaultThickness, thicknessNeg);
    }

    [Fact]
    public void ParseSpec_ZeroOrNegativeRadius_RetainsDefaultPositiveRadius()
    {
        var (radiusZero, thickness) = CircularProgressGeometry.ParseSpec("0,3");
        Assert.Equal(CircularProgressGeometry.DefaultRadius, radiusZero);
        Assert.Equal(3.0, thickness);

        var (radiusNeg, _) = CircularProgressGeometry.ParseSpec("-5,3");
        Assert.Equal(CircularProgressGeometry.DefaultRadius, radiusNeg);
    }

    [Fact]
    public void ComputeDash_ZeroPercent_ProducesFinitePositiveGapAndZeroDashWithoutNaN()
    {
        var (dash, gap) = CircularProgressGeometry.ComputeDash(0, 6, 2);
        Assert.Equal(0.0, dash);
        Assert.False(double.IsNaN(dash));
        Assert.False(double.IsInfinity(dash));
        Assert.True(gap > 0);
        Assert.False(double.IsNaN(gap));
        Assert.False(double.IsInfinity(gap));
    }

    [Fact]
    public void ComputeDash_OneHundredPercent_ProducesFiniteDashAndGap()
    {
        var (dash, gap) = CircularProgressGeometry.ComputeDash(100, 6, 2);
        Assert.True(dash > 0);
        Assert.False(double.IsNaN(dash));
        Assert.False(double.IsInfinity(dash));
        Assert.True(gap > dash);
    }

    [Fact]
    public void ComputeDash_ZeroOrNegativeThickness_NeverDividesByZeroOrProducesInfinity()
    {
        // Even if non-positive thickness reaches ComputeDash, it must fallback and avoid division by zero.
        var (dash, gap) = CircularProgressGeometry.ComputeDash(50, 6, 0);
        Assert.False(double.IsNaN(dash));
        Assert.False(double.IsInfinity(dash));
        Assert.False(double.IsNaN(gap));
        Assert.False(double.IsInfinity(gap));
        Assert.True(dash > 0);
    }
}
