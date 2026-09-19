using System;
using System.Collections.Generic;

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
    /// window's own. Reading the screen is only legitimate when the window is visible, restored, fully
    /// inside the desktop, and nothing is drawn over it.
    /// </summary>
    /// <param name="isVisible">The window's <c>IsWindowVisible</c> state.</param>
    /// <param name="isMinimized">The window's <c>IsIconic</c> state.</param>
    /// <param name="window">The window's screen rectangle.</param>
    /// <param name="virtualScreen">The bounding rectangle of every monitor.</param>
    /// <param name="windowsAbove">
    /// The rectangles of the windows painted above this one, nearest first. Windows that paint nothing
    /// (hidden, minimized, cloaked) must be left out by the caller.
    /// </param>
    public static WindowCaptureExposure Classify(
        bool isVisible,
        bool isMinimized,
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

        // Anything outside the desktop is never composited, so those pixels are undefined rather than
        // the window's; the same test rejects the empty rectangle a destroyed window leaves behind.
        if (!virtualScreen.Contains(window))
        {
            return WindowCaptureExposure.OffScreen;
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
    /// Scales one side of a capture. A side shorter than <c>1 / scale</c> pixels would round down to
    /// zero and <c>new Bitmap(0, h)</c> throws, so every scaled side keeps at least one pixel.
    /// </summary>
    public static int ScaleDimension(int value, double scale) => Math.Max(1, (int)Math.Round(value * scale));
}
