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

    /// <summary>
    /// Gets the left.
    /// </summary>
    public int Left { get; }

    /// <summary>
    /// Gets the top.
    /// </summary>
    public int Top { get; }

    /// <summary>
    /// Gets the right.
    /// </summary>
    public int Right { get; }

    /// <summary>
    /// Gets the bottom.
    /// </summary>
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

    /// <summary>
    /// Determines whether the specified object is equal to the current ScreenRect instance by comparing their properties.
    /// </summary>
    /// <param name="obj">The obj.</param>
    /// <returns>true if the operation succeeded; otherwise, false.</returns>
    public override bool Equals(object? obj) => obj is ScreenRect other && Equals(other);

    public override int GetHashCode() => (((((Left * 397) ^ Top) * 397) ^ Right) * 397) ^ Bottom;

    /// <summary>Renders the four edges as <c>(left,top)-(right,bottom)</c>.</summary>
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0},{1})-({2},{3})", Left, Top, Right, Bottom);

    public static bool operator ==(ScreenRect left, ScreenRect right) => left.Equals(right);

    public static bool operator !=(ScreenRect left, ScreenRect right) => !left.Equals(right);
}
