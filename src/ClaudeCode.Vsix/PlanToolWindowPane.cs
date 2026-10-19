using ClaudeCode.Core.ViewModels;
using ClaudeCode.Core.Views;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace ClaudeCode.Vsix;

/// <summary>"Implementation Plan" document tab: shows the plan Claude wants approved, with Proceed / Review.</summary>
[Guid(PackageGuids.PlanToolWindowPersistanceString)]
public sealed class PlanToolWindowPane : ToolWindowPane
{
    private readonly PlanDocumentView _view;
    private bool _disposed;

    public PlanToolWindowPane() : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = "Implementation Plan";
        _view = new PlanDocumentView();
        VsChatTheme.Apply(_view);
        _view.RefreshTheme();
        Content = _view;
        // Subscribe to the process-wide static event last, so a throw above cannot leave
        // VSColorTheme holding a pane whose Dispose(bool) will never run.
        VSColorTheme.ThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// Displays the specified plan review on the view by assigning it to the view&apos;s Plan property, ensuring the operation runs on the UI thread.
    /// </summary>
    /// <param name="plan">The plan.</param>
    public void ShowPlan(PlanReviewViewModel plan)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _view.Plan = plan;
    }

    /// <summary>
    /// Handles theme change notifications by synchronously reapplying the chat theme to the plan view on the main UI thread, and logs an error to the activity log if the refresh fails.
    /// </summary>
    /// <param name="e">The e.</param>
    private void OnThemeChanged(ThemeChangedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsChatTheme.Apply(_view);
                _view.RefreshTheme();
            });
        }
        catch (Exception exception)
        {
            ActivityLog.TryLogError("Claude Code", "Could not refresh the plan theme: " + exception);
        }
    }

    /// <summary>
    /// Releases the managed resources used by the control, unsubscribing from the theme change notifications and disposing the underlying view once, and then invokes the base implementation.
    /// </summary>
    /// <param name="disposing">The disposing.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            VSColorTheme.ThemeChanged -= OnThemeChanged;
            _view.Dispose();
        }

        base.Dispose(disposing);
    }
}
