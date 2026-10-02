using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// The pure decisions behind the VSIX <c>captureWindow</c> tool.
/// </summary>
public static class WindowCaptureRules
{
    /// <summary>
    /// Classifies whether the pixels currently on screen at <paramref name="window"/> are that
    /// window's own. Returns <see cref="WindowCaptureExposure.Exposed"/> only when the window is
    /// visible, restored, not cloaked, inside <paramref name="virtualScreen"/>, paints its whole
    /// rectangle, and no rectangle in <paramref name="windowsAbove"/> overlaps it.
    /// </summary>
    /// <param name="isVisible">The window's <c>IsWindowVisible</c> state.</param>
    /// <param name="isMinimized">The window's <c>IsIconic</c> state.</param>
    /// <param name="isCloaked">The window's <c>DWMWA_CLOAKED</c> state.</param>
    /// <param name="paintsWholeRectangle">
    /// False when the window is layered (<c>WS_EX_LAYERED</c>/<c>WS_EX_TRANSPARENT</c>) or
    /// region-shaped.
    /// </param>
    /// <param name="window">The window's screen rectangle.</param>
    /// <param name="virtualScreen">The bounding rectangle of every monitor.</param>
    /// <param name="windowsAbove">
    /// The rectangles of the windows painted above this one, nearest first, excluding windows that
    /// paint nothing (hidden, minimized, cloaked).
    /// </param>
    public static WindowCaptureExposure Classify(
        bool isVisible,
        bool isMinimized,
        bool isCloaked,
        bool paintsWholeRectangle,
        ScreenRect window,
        ScreenRect virtualScreen,
        IEnumerable<ScreenRect> windowsAbove)
    {
        if (windowsAbove is null)
        {
            throw new ArgumentNullException(nameof(windowsAbove));
        }

        if (!isVisible)
        {
            return WindowCaptureExposure.Hidden;
        }

        if (isMinimized)
        {
            return WindowCaptureExposure.Minimized;
        }

        if (isCloaked)
        {
            return WindowCaptureExposure.Cloaked;
        }

        if (!virtualScreen.Contains(window))
        {
            return WindowCaptureExposure.OffScreen;
        }

        if (!paintsWholeRectangle)
        {
            return WindowCaptureExposure.Translucent;
        }

        foreach (var above in windowsAbove)
        {
            if (above.Overlaps(window))
            {
                return WindowCaptureExposure.Occluded;
            }
        }

        return WindowCaptureExposure.Exposed;
    }

    /// <summary>
    /// The factor that brings the longest side of a <paramref name="width"/> x <paramref name="height"/>
    /// capture down to <paramref name="maxSide"/>. Never enlarges a window that already fits.
    /// </summary>
    public static double ScaleForLongestSide(int width, int height, int maxSide)
    {
        if (maxSide <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSide), maxSide, "The longest-side limit must be positive.");
        }

        var longest = Math.Max(width, height);

        return longest <= maxSide ? 1.0 : (double)maxSide / longest;
    }

    /// <summary>
    /// True when <paramref name="window"/> encloses at least one pixel and is no larger than
    /// <paramref name="virtualScreen"/> in either direction.
    /// </summary>
    public static bool HasCapturableSize(ScreenRect window, ScreenRect virtualScreen) =>
        !window.IsEmpty && (window.Width <= virtualScreen.Width) && (window.Height <= virtualScreen.Height);

    /// <summary>
    /// The rectangle to read pixels from: <paramref name="extendedFrame"/> (the window's DWM frame)
    /// when it is non-empty and lies inside <paramref name="window"/>; otherwise
    /// <paramref name="window"/>.
    /// </summary>
    public static ScreenRect PaintedBounds(ScreenRect window, ScreenRect extendedFrame) =>
        !extendedFrame.IsEmpty && window.Contains(extendedFrame) ? extendedFrame : window;

    /// <summary>
    /// Scales one side of a capture, rounding to the nearest pixel and keeping at least one pixel.
    /// </summary>
    public static int ScaleDimension(int value, double scale) => Math.Max(1, (int)Math.Round(value * scale));
}
