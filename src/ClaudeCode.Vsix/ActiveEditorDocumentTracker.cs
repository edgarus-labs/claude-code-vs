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

/// <summary>
/// Tracks the currently active editor document and its text view by monitoring window frame activation and text view lifecycle events, exposing whether an active document is available.
/// </summary>
internal sealed class ActiveEditorDocumentTracker : IDisposable
{
    private readonly WindowEvents _windowEvents;
    private DocumentView? _lastDocumentView;
    private int _activationVersion;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the ActiveEditorDocumentTracker class, enforcing UI thread affinity and subscribing to the Visual Studio window events to track active frame changes.
    /// </summary>
    public ActiveEditorDocumentTracker()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _windowEvents = VS.Events.WindowEvents;
        _windowEvents.ActiveFrameChanged += OnActiveFrameChanged;
    }

    /// <summary>Raised on the UI thread whenever the tracked document view changes.</summary>
    public event EventHandler? ActiveDocumentChanged;

    /// <summary>
    /// Gets a value indicating whether has active document.
    /// </summary>
    public bool HasActiveDocument => !_disposed && _lastDocumentView?.TextView is { IsClosed: false } && _lastDocumentView.TextBuffer is not null;

    /// <summary>
    /// Asynchronously initializes the tracker by seeding the last active document view on the main thread, provided no newer editor activation has occurred and the view is a valid text view.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var version = _activationVersion;
        var view = await VS.Documents.GetActiveDocumentViewAsync();
        cancellationToken.ThrowIfCancellationRequested();

        // An editor activation during initialization is newer than this seed.
        if (!_disposed && version == _activationVersion && IsOpenTextView(view))
        {
            SetLastDocumentView(view);
        }
    }

    /// <summary>
    /// Asynchronously captures the live, unsaved text of the active editor document on the main thread, returning a snapshot with its normalized full file path, or null if no valid rooted local file document is open or the service has been disposed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the editor document snapshot?.</returns>
    public async Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var view = _lastDocumentView;
        if (_disposed || !IsOpenTextView(view))
        {
            return null;
        }

        // Document.FilePath follows Save As/rename; DocumentView.FilePath is a cached value.
        var path = view!.Document?.FilePath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ||
            !Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return null;
        }

        // Read the live, unsaved buffer only now, never at activation or from disk.
        return new EditorDocumentSnapshot(Path.GetFullPath(path), view.TextBuffer!.CurrentSnapshot.GetText());
    }

    /// <summary>
    /// Handles active frame changes on the UI thread by incrementing the activation version and, if the instance has not been disposed, recording the open text view from the new or old frame as the last document view.
    /// </summary>
    /// <param name="args">The args.</param>
    private void OnActiveFrameChanged(ActiveFrameChangeEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed)
        {
            return;
        }

        ++_activationVersion;
        // Resolve synchronously on the UI thread: a delayed old activation cannot win.
        // The outgoing frame covers editor -> chat even when startup missed the editor.
        var view = GetOpenTextView(args.NewFrame) ?? GetOpenTextView(args.OldFrame);
        if (view is not null)
        {
            SetLastDocumentView(view);
        }
    }

    /// <summary>
    /// Retrieves the open text document view associated with the specified window frame, returning null if the frame is null, no text view is available, the view is not open, or the frame is torn down during activation.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The document view? result.</returns>
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
            // A frame can be torn down while VS delivers the activation notification.
            return null;
        }
    }

    /// <summary>
    /// Determines whether the specified document view has an open text view and a non-null text buffer, ensuring the check runs on the UI thread.
    /// </summary>
    /// <param name="view">The view.</param>
    /// <returns>true if the condition is met; otherwise, false.</returns>
    private static bool IsOpenTextView(DocumentView? view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return view?.TextView is { IsClosed: false } && view.TextBuffer is not null;
    }

    /// <summary>
    /// Updates the last document view, unsubscribes from the previous view&apos;s Closed event, subscribes to the new view&apos;s Closed event, and raises the ActiveDocumentChanged event if the view is not null and the instance is not disposed.
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

        // Dispose() calls this with null after setting _disposed; a teardown notification has no
        // subscriber that needs it (HasActiveDocument already reports false) and is a latent trap.
        if (!_disposed)
        {
            ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Clears the last document view reference when the associated text view is closed.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="args">The args.</param>
    private void OnTextViewClosed(object? sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ReferenceEquals(sender, _lastDocumentView?.TextView))
        {
            SetLastDocumentView(null);
        }
    }

    /// <summary>
    /// Releases resources and unsubscribes from frame change events.
    /// </summary>
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
