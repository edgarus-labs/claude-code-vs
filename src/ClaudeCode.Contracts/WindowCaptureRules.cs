using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeCode.Contracts;

/// <summary>A desktop rectangle in physical screen pixels, right/bottom exclusive like the Win32 <c>RECT</c> it is built from.</summary>
public readonly struct ScreenRect : IEquatable<ScreenRect>
{
    public ScreenRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Left { get; }

    public int Top { get; }

    public int Right { get; }

    public int Bottom { get; }

    /// <summary>The width in pixels; zero or negative for an empty rectangle.</summary>
    public int Width => Right - Left;

    /// <summary>The height in pixels; zero or negative for an empty rectangle.</summary>
    public int Height => Bottom - Top;

    /// <summary>True when the rectangle encloses no pixel at all.</summary>
    public bool IsEmpty => (Right <= Left) || (Bottom <= Top);

    /// <summary>True when the two rectangles share at least one pixel; merely touching edges do not.</summary>
    public bool Overlaps(ScreenRect other) =>
        !IsEmpty && !other.IsEmpty && (other.Left < Right) && (other.Right > Left) && (other.Top < Bottom) && (other.Bottom > Top);

    /// <summary>True when every pixel of <paramref name="other"/> lies inside this rectangle.</summary>
    public bool Contains(ScreenRect other) =>
        !other.IsEmpty && (other.Left >= Left) && (other.Top >= Top) && (other.Right <= Right) && (other.Bottom <= Bottom);

    public bool Equals(ScreenRect other) =>
        (Left == other.Left) && (Top == other.Top) && (Right == other.Right) && (Bottom == other.Bottom);

    public override bool Equals(object? obj) => obj is ScreenRect other && Equals(other);

    public override int GetHashCode() => (((((Left * 397) ^ Top) * 397) ^ Right) * 397) ^ Bottom;

    /// <summary>Renders the four edges so a failed comparison names the rectangle it got.</summary>
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0},{1})-({2},{3})", Left, Top, Right, Bottom);

    public static bool operator ==(ScreenRect left, ScreenRect right) => left.Equals(right);

    public static bool operator !=(ScreenRect left, ScreenRect right) => !left.Equals(right);
}

/// <summary>Why a window's own pixels can - or cannot - be read off the screen.</summary>
public enum WindowCaptureExposure
{
    /// <summary>Every pixel of the window rectangle is the window's own.</summary>
    Exposed,

    /// <summary>The window is not visible, so the desktop shows something else at its rectangle.</summary>
    Hidden,

    /// <summary>The window is minimized and paints nothing.</summary>
    Minimized,

    /// <summary>The window is DWM-cloaked - parked on another virtual desktop, or suspended - so it
    /// keeps a full on-screen rectangle it never paints in.</summary>
    Cloaked,

    /// <summary>The window does not paint its whole rectangle opaquely: it is layered, so the screen
    /// holds a blend with whatever is behind it, or region-shaped, so the pixels outside its region
    /// belong to the windows below.</summary>
    Translucent,

    /// <summary>The rectangle is empty or reaches outside the desktop, where no pixels are painted.</summary>
    OffScreen,

    /// <summary>Another window is above it and overlaps, so the screen holds that window's pixels.</summary>
    Occluded,
}

/// <summary>
/// The pure decisions behind the VSIX <c>captureWindow</c> tool. They live in this shared assembly
/// rather than next to their single caller because <c>ClaudeCode.Vsix</c> has no test project, and
/// <see cref="Classify"/> is the security gate that keeps a screenshot request from returning another
/// application's pixels to the agent.
/// </summary>
public static class WindowCaptureRules
{
    /// <summary>
    /// Decides whether the pixels currently on screen at <paramref name="window"/> are provably that
    /// window's own. That needs both halves of the premise: the window has to paint its own rectangle
    /// opaquely (visible, restored, not cloaked, not layered or region-shaped) and nothing may be drawn
    /// over it. A window that does not paint is otherwise indistinguishable from one that does, and the
    /// desktop shows whatever is behind it.
    /// </summary>
    /// <param name="isVisible">The window's <c>IsWindowVisible</c> state.</param>
    /// <param name="isMinimized">The window's <c>IsIconic</c> state.</param>
    /// <param name="isCloaked">
    /// The window's <c>DWMWA_CLOAKED</c> state: composited nowhere, while keeping a visible, restored,
    /// full-size rectangle - a window on another virtual desktop, or a suspended app.
    /// </param>
    /// <param name="paintsWholeRectangle">
    /// False when the window is layered (<c>WS_EX_LAYERED</c>/<c>WS_EX_TRANSPARENT</c>), so the screen
    /// holds a blend with what is behind it, or region-shaped, so the pixels outside its region belong
    /// to the windows below.
    /// </param>
    /// <param name="window">The window's screen rectangle.</param>
    /// <param name="virtualScreen">The bounding rectangle of every monitor.</param>
    /// <param name="windowsAbove">
    /// The rectangles of the windows painted above this one, nearest first. Windows that paint nothing
    /// (hidden, minimized, cloaked) must be left out by the caller.
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

        // The target's own paint state is decided before the geometry and the z-order, because it is
        // the answer the caller can act on: "the window is on another virtual desktop" rather than
        // "bring it to the front", which would not help.
        if (isCloaked)
        {
            return WindowCaptureExposure.Cloaked;
        }

        // Anything outside the desktop is never composited, so those pixels are undefined rather than
        // the window's; the same test rejects the empty rectangle a destroyed window leaves behind.
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
    /// True when <paramref name="window"/> is a rectangle worth allocating a capture surface for: it
    /// encloses at least one pixel and is no larger than the desktop in either direction. The surface
    /// is allocated from the window's own rectangle before any downscale applies, so a window sized
    /// past the desktop - trivial for a debuggee to do to itself - would ask for gigabytes of unmanaged
    /// bitmap, and a rectangle with no area would be clamped up into a 1x1 "screenshot" of one pixel
    /// and reported as a success. This bounds the surface only; where the pixels may be read from is
    /// <see cref="Classify"/>'s decision.
    /// </summary>
    public static bool HasCapturableSize(ScreenRect window, ScreenRect virtualScreen) =>
        !window.IsEmpty && (window.Width <= virtualScreen.Width) && (window.Height <= virtualScreen.Height);

    /// <summary>
    /// The rectangle to read pixels from for a window whose rectangle is <paramref name="window"/> and
    /// whose DWM frame is <paramref name="extendedFrame"/>. The window rectangle is larger than the
    /// pixels the window owns: the invisible resize border lies outside the frame and is transparent,
    /// and rounded corners leave the corner pixels to the window below. The frame is used when it is
    /// known and lies inside the classified rectangle; otherwise - an unavailable
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>, or one reaching past what was classified - the window
    /// rectangle stands, since reading outside it was never proven safe.
    /// </summary>
    public static ScreenRect PaintedBounds(ScreenRect window, ScreenRect extendedFrame) =>
        !extendedFrame.IsEmpty && window.Contains(extendedFrame) ? extendedFrame : window;

    /// <summary>
    /// Scales one side of a capture. A side shorter than <c>1 / scale</c> pixels would round down to
    /// zero and <c>new Bitmap(0, h)</c> throws, so every scaled side keeps at least one pixel.
    /// </summary>
    public static int ScaleDimension(int value, double scale) => Math.Max(1, (int)Math.Round(value * scale));
}
