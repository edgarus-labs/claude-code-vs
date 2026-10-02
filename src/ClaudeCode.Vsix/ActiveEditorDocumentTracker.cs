using ClaudeCode.Core.ViewModels;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix;

internal sealed class ActiveEditorDocumentTracker : IDisposable
{
    private readonly WindowEvents _windowEvents;
    private DocumentView? _lastDocumentView;
    private int _activationVersion;
    private bool _disposed;

    public ActiveEditorDocumentTracker()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _windowEvents = VS.Events.WindowEvents;
        _windowEvents.ActiveFrameChanged += OnActiveFrameChanged;
    }

    /// <summary>
    /// Occurs when active document changed.
    /// </summary>
    public event EventHandler? ActiveDocumentChanged;

    /// <summary>
    /// Gets a value indicating whether has active document.
    /// </summary>
    public bool HasActiveDocument => !_disposed && _lastDocumentView?.TextView is { IsClosed: false } && _lastDocumentView.TextBuffer is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var version = _activationVersion;
        var view = await VS.Documents.GetActiveDocumentViewAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (!_disposed && version == _activationVersion && IsOpenTextView(view))
        {
            SetLastDocumentView(view);
        }
    }

    public async Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var view = _lastDocumentView;
        if (_disposed || !IsOpenTextView(view))
        {
            return null;
        }

        var path = view!.Document?.FilePath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ||
            !Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return null;
        }

        return new EditorDocumentSnapshot(Path.GetFullPath(path), view.TextBuffer!.CurrentSnapshot.GetText());
    }

    private void OnActiveFrameChanged(ActiveFrameChangeEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed)
        {
            return;
        }

        ++_activationVersion;
        var view = GetOpenTextView(args.NewFrame) ?? GetOpenTextView(args.OldFrame);
        if (view is not null)
        {
            SetLastDocumentView(view);
        }
    }

    private static DocumentView? GetOpenTextView(WindowFrame? frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (frame is null)
        {
            return null;
        }

        try
        {
            var view = VsShellUtilities.GetTextView(frame)?.ToDocumentView();
            return IsOpenTextView(view) ? view : null;
        }
        catch (Exception ex) when (ex is COMException || ex is NullReferenceException || ex is InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsOpenTextView(DocumentView? view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return view?.TextView is { IsClosed: false } && view.TextBuffer is not null;
    }

    /// <summary>
    /// Updates the stored last document view, detaches the Closed handler from the previous view, attaches it to the new view, and raises the ActiveDocumentChanged event when not disposed.
    /// </summary>
    /// <param name="view">The view.</param>
    private void SetLastDocumentView(DocumentView? view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_lastDocumentView?.TextView is { } previous)
        {
            previous.Closed -= OnTextViewClosed;
        }

        _lastDocumentView = view;
        if (view?.TextView is { } current)
        {
            current.Closed += OnTextViewClosed;
        }

        if (!_disposed)
        {
            ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnTextViewClosed(object? sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ReferenceEquals(sender, _lastDocumentView?.TextView))
        {
            SetLastDocumentView(null);
        }
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _windowEvents.ActiveFrameChanged -= OnActiveFrameChanged;
        SetLastDocumentView(null);
    }
}
