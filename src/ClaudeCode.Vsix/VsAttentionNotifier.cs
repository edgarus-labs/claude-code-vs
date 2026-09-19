using Microsoft.VisualStudio.Shell;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClaudeCode.Vsix;

/// <summary>Shows a Windows notification (tray balloon → Action Center toast) when Claude needs the
/// user and Visual Studio is not the foreground window, like the VS Code extension's notifications.
/// Clicking the notification brings Visual Studio and the Claude Code window to the front.</summary>
internal sealed class VsAttentionNotifier : IDisposable
{
    private readonly NotifyIcon _icon;
    /// <summary>The icon cloned from the packaged bitmap, which owns a native HICON; null when the
    /// stock <see cref="SystemIcons.Information"/> (shared, never disposed) is in use instead.</summary>
    private readonly Icon? _ownedIcon;
    private readonly Action _activateChatWindow;
    private bool _disposed;

    /// <param name="activateChatWindow">Brings the Claude Code window to the front. Invoked on the
    /// UI thread when the user clicks the notification.</param>
    public VsAttentionNotifier(Action activateChatWindow)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _activateChatWindow = activateChatWindow ?? throw new ArgumentNullException(nameof(activateChatWindow));
        _ownedIcon = LoadIcon();
        _icon = new NotifyIcon { Icon = _ownedIcon ?? SystemIcons.Information, Text = "Claude Code for Visual Studio", Visible = false };
        _icon.BalloonTipClicked += OnBalloonClicked;
        _icon.BalloonTipClosed += (_, __) => HideIcon();
    }

    public void Notify(string title, string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed || IsVisualStudioForeground()) return;
        _icon.Visible = true;
        _icon.ShowBalloonTip(8000, title, string.IsNullOrWhiteSpace(message) ? " " : message, ToolTipIcon.None);
    }

    private static bool IsVisualStudioForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(foreground, out var pid);
        using var current = Process.GetCurrentProcess();
        return pid == (uint)current.Id;
    }

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        HideIcon();
        // Fire-and-forget by design: a click on a system notification has no caller to await it;
        // failures are logged inside, and FileAndForget reports the fault to VS telemetry.
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                using var current = Process.GetCurrentProcess();
                var handle = current.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                    SetForegroundWindow(handle);
                }

                _activateChatWindow();
            }
            catch (Exception exception)
            {
                ActivityLog.TryLogError("Claude Code", "Could not activate Visual Studio from the notification: " + exception);
            }
        }).FileAndForget("claudecode/notification-activate");
#pragma warning restore VSSDK007
    }

    private void HideIcon()
    {
        if (!_disposed) _icon.Visible = false;
    }

    private static Icon? LoadIcon()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(VsAttentionNotifier).Assembly.Location)!, "Resources", "Icon.png");
            if (File.Exists(path))
            {
                using var bitmap = new Bitmap(path);
                using var sized = new Bitmap(bitmap, new Size(32, 32));

                // GetHicon allocates a native handle that Icon.FromHandle wraps with
                // ownHandle: false - disposing that Icon never calls DestroyIcon. Clone into a
                // managed icon that owns its own handle, then release the native one.
                IntPtr nativeHandle = sized.GetHicon();
                try
                {
                    using Icon unowned = Icon.FromHandle(nativeHandle);
                    return (Icon)unowned.Clone();
                }
                finally
                {
                    _ = DestroyIcon(nativeHandle);
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the stock icon.
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        // NotifyIcon.Dispose does not dispose the Icon assigned to it.
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
}
