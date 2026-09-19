using System;
using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// The pure half of the VSIX <c>captureWindow</c> tool. <see cref="WindowCaptureRules.Classify"/> is the
/// gate that decides whether a screen blit may happen at all, so every rejection path is pinned here;
/// the scale arithmetic is pinned because a wrong result is invisible (a silently tiny image, or a zero
/// dimension that throws inside GDI+).
/// </summary>
public sealed class WindowCaptureRulesTests
{
    private static readonly ScreenRect _virtualScreen = new ScreenRect(0, 0, 3840, 2160);
    private static readonly ScreenRect _window = new ScreenRect(100, 100, 1100, 700);
    private static readonly ScreenRect[] _nothingAbove = Array.Empty<ScreenRect>();

    [Fact]
    public void Classify_VisibleWindowWithNothingOverIt_IsExposed()
    {
        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, _window, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Exposed, exposure);
    }

    [Fact]
    public void Classify_HiddenWindow_IsHiddenEvenWithPerfectGeometry()
    {
        var exposure = WindowCaptureRules.Classify(isVisible: false, isMinimized: false, _window, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Hidden, exposure);
    }

    [Fact]
    public void Classify_MinimizedWindow_IsMinimized()
    {
        // A minimized window reports visible and sits at the shell's off-screen parking coordinates.
        var parked = new ScreenRect(-32000, -32000, -31840, -31980);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: true, parked, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Minimized, exposure);
    }

    [Fact]
    public void Classify_WindowHangingOverTheEdgeOfTheDesktop_IsOffScreen()
    {
        var overhanging = new ScreenRect(3000, 100, 4000, 700);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, overhanging, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.OffScreen, exposure);
    }

    [Fact]
    public void Classify_WindowFillingTheWholeDesktop_IsExposed()
    {
        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, _virtualScreen, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Exposed, exposure);
    }

    [Fact]
    public void Classify_WindowWithNoArea_IsOffScreen()
    {
        var destroyed = new ScreenRect(0, 0, 0, 0);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, destroyed, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.OffScreen, exposure);
    }

    [Fact]
    public void Classify_WindowAboveOverlappingASinglePixel_IsOccluded()
    {
        var overlapsCorner = new ScreenRect(1099, 699, 2000, 1400);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, _window, _virtualScreen, new[] { overlapsCorner });

        Assert.Equal(WindowCaptureExposure.Occluded, exposure);
    }

    [Fact]
    public void Classify_WindowAboveTouchingTheEdgeOnly_IsExposed()
    {
        var sideBySide = new ScreenRect(1100, 100, 2000, 700);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, _window, _virtualScreen, new[] { sideBySide });

        Assert.Equal(WindowCaptureExposure.Exposed, exposure);
    }

    [Fact]
    public void Classify_ZeroSizedWindowAbove_IsIgnored()
    {
        var pointSized = new ScreenRect(200, 200, 200, 200);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, _window, _virtualScreen, new[] { pointSized });

        Assert.Equal(WindowCaptureExposure.Exposed, exposure);
    }

    [Fact]
    public void Classify_SecondWindowAboveCoversTheTarget_IsOccluded()
    {
        var elsewhere = new ScreenRect(2000, 1500, 2500, 1900);
        var onTop = new ScreenRect(0, 0, 3840, 2160);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, _window, _virtualScreen, new[] { elsewhere, onTop });

        Assert.Equal(WindowCaptureExposure.Occluded, exposure);
    }

    [Fact]
    public void ScaleForLongestSide_WindowSmallerThanTheLimit_IsNotUpscaled()
    {
        Assert.Equal(1.0, WindowCaptureRules.ScaleForLongestSide(800, 600, 1920));
    }

    [Fact]
    public void ScaleForLongestSide_WindowExactlyAtTheLimit_IsNotScaled()
    {
        Assert.Equal(1.0, WindowCaptureRules.ScaleForLongestSide(1920, 1080, 1920));
    }

    [Fact]
    public void ScaleForLongestSide_TallWindow_ScalesByItsHeight()
    {
        Assert.Equal(0.5, WindowCaptureRules.ScaleForLongestSide(600, 3840, 1920));
    }

    [Fact]
    public void ScaleForLongestSide_WideWindow_ScalesByItsWidth()
    {
        Assert.Equal(0.5, WindowCaptureRules.ScaleForLongestSide(3840, 600, 1920));
    }

    [Fact]
    public void ScaleDimension_SideThatWouldRoundToZero_KeepsOnePixel()
    {
        Assert.Equal(1, WindowCaptureRules.ScaleDimension(3, 0.05));
    }

    [Fact]
    public void ScaleDimension_HalfScale_HalvesTheSide()
    {
        Assert.Equal(500, WindowCaptureRules.ScaleDimension(1000, 0.5));
    }
}
