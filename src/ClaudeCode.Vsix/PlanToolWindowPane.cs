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
        VSColorTheme.ThemeChanged += OnThemeChanged;
        Content = _view;
    }

    public void ShowPlan(PlanReviewViewModel plan)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _view.Plan = plan;
    }

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
