using System;
using System.IO;

namespace ClaudeCode.Core.Views;

internal static class WebView2Profile
{
    /// <summary>
    /// Gets the user data folder.
    /// </summary>
    internal static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EdgarusLabs", "ClaudeCode", "WebView2");
}
