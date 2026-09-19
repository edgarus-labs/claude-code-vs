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
        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, _window, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Exposed, exposure);
    }

    [Fact]
    public void Classify_HiddenWindow_IsHiddenEvenWithPerfectGeometry()
    {
        var exposure = WindowCaptureRules.Classify(isVisible: false, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, _window, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Hidden, exposure);
    }

    [Fact]
    public void Classify_MinimizedWindow_IsMinimized()
    {
        // A minimized window reports visible and sits at the shell's off-screen parking coordinates.
        var parked = new ScreenRect(-32000, -32000, -31840, -31980);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: true, isCloaked: false, paintsWholeRectangle: true, parked, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Minimized, exposure);
    }

    [Fact]
    public void Classify_CloakedWindow_IsCloakedEvenWithPerfectGeometry()
    {
        // A window parked on another virtual desktop keeps IsWindowVisible, a restored state and its
        // full on-screen rectangle; the desktop at that rectangle shows the desktop the user switched to.
        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: true, paintsWholeRectangle: true, _window, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Cloaked, exposure);
    }

    [Fact]
    public void Classify_CloakedWindowWithAnotherWindowOverIt_IsCloakedRatherThanOccluded()
    {
        // The target's own paint state is the actionable answer: "switch back to the desktop it is on"
        // rather than "bring it to the front", which would not help.
        var onTop = new ScreenRect(0, 0, 3840, 2160);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: true, paintsWholeRectangle: true, _window, _virtualScreen, new[] { onTop });

        Assert.Equal(WindowCaptureExposure.Cloaked, exposure);
    }

    [Fact]
    public void Classify_WindowThatDoesNotPaintItsWholeRectangle_IsTranslucent()
    {
        // A layered window is blended with what is behind it and a region-shaped one shows the windows
        // below through the pixels outside its region: nothing is above it, and the pixels are still
        // not all its own.
        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: false, _window, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Translucent, exposure);
    }

    [Fact]
    public void Classify_WindowHangingOverTheEdgeOfTheDesktop_IsOffScreen()
    {
        var overhanging = new ScreenRect(3000, 100, 4000, 700);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, overhanging, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.OffScreen, exposure);
    }

    [Fact]
    public void Classify_WindowFillingTheWholeDesktop_IsExposed()
    {
        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, _virtualScreen, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Exposed, exposure);
    }

    [Fact]
    public void Classify_WindowOnAMonitorLeftOfThePrimary_IsExposed()
    {
        // A second monitor to the left gives the virtual screen a negative origin; the rule must be
        // plain arithmetic on signed coordinates, not a distance from zero.
        var multiMonitor = new ScreenRect(-1920, 0, 1920, 1080);
        var onTheLeftMonitor = new ScreenRect(-1800, 100, -800, 700);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, onTheLeftMonitor, multiMonitor, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.Exposed, exposure);
    }

    [Fact]
    public void Classify_WindowPastTheLeftEdgeOfANegativeOriginVirtualScreen_IsOffScreen()
    {
        var multiMonitor = new ScreenRect(-1920, 0, 1920, 1080);
        var pastTheLeftEdge = new ScreenRect(-2000, 100, -800, 700);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, pastTheLeftEdge, multiMonitor, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.OffScreen, exposure);
    }

    [Fact]
    public void Classify_WindowWithNoArea_IsOffScreen()
    {
        var destroyed = new ScreenRect(0, 0, 0, 0);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, destroyed, _virtualScreen, _nothingAbove);

        Assert.Equal(WindowCaptureExposure.OffScreen, exposure);
    }

    [Fact]
    public void Classify_WindowAboveOverlappingASinglePixel_IsOccluded()
    {
        var overlapsCorner = new ScreenRect(1099, 699, 2000, 1400);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, _window, _virtualScreen, new[] { overlapsCorner });

        Assert.Equal(WindowCaptureExposure.Occluded, exposure);
    }

    [Fact]
    public void Classify_WindowAboveTouchingTheEdgeOnly_IsExposed()
    {
        var sideBySide = new ScreenRect(1100, 100, 2000, 700);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, _window, _virtualScreen, new[] { sideBySide });

        Assert.Equal(WindowCaptureExposure.Exposed, exposure);
    }

    [Fact]
    public void Classify_ZeroSizedWindowAbove_IsIgnored()
    {
        var pointSized = new ScreenRect(200, 200, 200, 200);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, _window, _virtualScreen, new[] { pointSized });

        Assert.Equal(WindowCaptureExposure.Exposed, exposure);
    }

    [Fact]
    public void Classify_SecondWindowAboveCoversTheTarget_IsOccluded()
    {
        var elsewhere = new ScreenRect(2000, 1500, 2500, 1900);
        var onTop = new ScreenRect(0, 0, 3840, 2160);

        var exposure = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, _window, _virtualScreen, new[] { elsewhere, onTop });

        Assert.Equal(WindowCaptureExposure.Occluded, exposure);
    }

    [Fact]
    public void Classify_WithoutTheListOfWindowsAbove_Throws()
    {
        // The caller passes null only by mistake, and an empty list is the dangerous default: silently
        // reading it as "nothing is above" would classify every covered window as Exposed.
        var error = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = WindowCaptureRules.Classify(isVisible: true, isMinimized: false, isCloaked: false, paintsWholeRectangle: true, _window, _virtualScreen, windowsAbove: null!);
        });

        Assert.Equal("windowsAbove", error.ParamName);
    }

    [Fact]
    public void HasCapturableSize_WindowSmallerThanTheDesktop_IsCapturable()
    {
        Assert.True(WindowCaptureRules.HasCapturableSize(_window, _virtualScreen));
    }

    [Fact]
    public void HasCapturableSize_WindowExactlyTheSizeOfTheDesktop_IsCapturable()
    {
        Assert.True(WindowCaptureRules.HasCapturableSize(_virtualScreen, _virtualScreen));
    }

    [Fact]
    public void HasCapturableSize_WindowWiderThanTheDesktop_IsNotCapturable()
    {
        // A window may legitimately be sized past the desktop, and the surface is allocated from its
        // rectangle before any downscale applies: 30000x20000 asks GDI+ for ~1.7 GB inside devenv.
        var enormous = new ScreenRect(0, 0, 30_000, 1080);

        Assert.False(WindowCaptureRules.HasCapturableSize(enormous, _virtualScreen));
    }

    [Fact]
    public void HasCapturableSize_WindowTallerThanTheDesktop_IsNotCapturable()
    {
        var enormous = new ScreenRect(0, 0, 1920, 20_000);

        Assert.False(WindowCaptureRules.HasCapturableSize(enormous, _virtualScreen));
    }

    [Fact]
    public void HasCapturableSize_WindowWithNoArea_IsNotCapturable()
    {
        // Clamping an empty rectangle up to 1x1 would return the top-left pixel of the primary
        // monitor as a successful screenshot.
        Assert.False(WindowCaptureRules.HasCapturableSize(new ScreenRect(0, 0, 1920, 0), _virtualScreen));
    }

    [Fact]
    public void HasCapturableSize_DesktopSizedWindowHangingOverTheEdge_IsCapturable()
    {
        // This rule bounds the surface only. Whether the rectangle is a legitimate place to read
        // pixels from is Classify's decision, and PrintWindow does not need the window on screen.
        var overhanging = new ScreenRect(3000, 100, 4000, 700);

        Assert.True(WindowCaptureRules.HasCapturableSize(overhanging, _virtualScreen));
    }

    [Fact]
    public void PaintedBounds_FrameInsideTheWindowRectangle_IsTheFrame()
    {
        // The window rectangle includes the invisible resize border, which is outside the DWM frame
        // and transparent: the window below is composited there.
        var frame = new ScreenRect(107, 100, 1093, 693);

        Assert.Equal(frame, WindowCaptureRules.PaintedBounds(_window, frame));
    }

    [Fact]
    public void PaintedBounds_WithoutAFrame_IsTheWindowRectangle()
    {
        Assert.Equal(_window, WindowCaptureRules.PaintedBounds(_window, default));
    }

    [Fact]
    public void PaintedBounds_FrameReachingOutsideTheWindowRectangle_IsTheWindowRectangle()
    {
        // Only the window rectangle was classified, so a frame that reaches past it is never blitted.
        var wider = new ScreenRect(90, 100, 1200, 700);

        Assert.Equal(_window, WindowCaptureRules.PaintedBounds(_window, wider));
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
