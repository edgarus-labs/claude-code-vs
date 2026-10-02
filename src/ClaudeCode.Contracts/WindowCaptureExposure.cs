using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeCode.Contracts;

/// <summary>Whether a window's own pixels can be read off the screen, and if not, which condition prevents it.</summary>
public enum WindowCaptureExposure
{
    /// <summary>Every pixel of the window rectangle is the window's own.</summary>
    Exposed,

    /// <summary>The window is not visible, so the desktop shows something else at its rectangle.</summary>
    Hidden,

    /// <summary>The window is minimized and paints nothing.</summary>
    Minimized,

    /// <summary>The window is DWM-cloaked (on another virtual desktop, or suspended) and keeps a full
    /// on-screen rectangle it never paints in.</summary>
    Cloaked,

    /// <summary>The window does not paint its whole rectangle opaquely: it is layered or
    /// region-shaped.</summary>
    Translucent,

    /// <summary>The rectangle is empty or reaches outside the desktop, where no pixels are painted.</summary>
    OffScreen,

    /// <summary>Another window above it overlaps its rectangle.</summary>
    Occluded,
}
