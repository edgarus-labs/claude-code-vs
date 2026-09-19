using System;
using System.IO;

namespace ClaudeCode.Core.Views;

/// <summary>Where every WebView2 control in this extension keeps its browser profile.</summary>
internal static class WebView2Profile
{
    /// <summary>
    /// One user data folder for the transcript and plan views, in every devenv instance. WebView2
    /// controls that use the same folder share one browser process (in the same host or across
    /// hosts), and that is wanted: one Edge runtime and one cache per user rather than one per
    /// pane, and a stable path the runtime can reuse across sessions - a folder per process would
    /// pile up profiles that nothing ever removes. The cost is that a browser-process crash surfaces
    /// as <c>BrowserProcessExited</c> in every pane at once; each fails closed with a notice, the
    /// same outcome it would have had alone. Not under %TEMP%: disk cleanup may delete that under a
    /// running devenv, and this extension has no reason to touch the user's own Edge profile.
    /// </summary>
    internal static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EdgarusLabs", "ClaudeCode", "WebView2");
}
