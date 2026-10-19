using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels;

/// <summary>One workspace file the agent has written during the current session, with the content it
/// had before the first write so the user can reject (restore) or accept (keep) the change.</summary>
public sealed class ChangedFileViewModel : ObservableObject
{
    private int _addedLines;
    private int _removedLines;
    private bool _canRevert = true;

    /// <summary>
    /// Initializes a new instance of the ChangedFileViewModel class with the specified file path, original text, accept and reject actions, and optional tool call identifier, throwing an ArgumentNullException if the file path is null.
    /// </summary>
    /// <param name="fullPath">The full path.</param>
    /// <param name="originalText">The original text.</param>
    /// <param name="accept">The accept.</param>
    /// <param name="reject">The reject.</param>
    /// <param name="createdByToolCallId">The unique identifier of the created by tool call.</param>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
    public ChangedFileViewModel(string fullPath, string? originalText, Func<ChangedFileViewModel, Task> accept, Func<ChangedFileViewModel, Task> reject, string? createdByToolCallId)
    {
        FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
        OriginalText = originalText;
        CreatedByToolCallId = createdByToolCallId;
        Name = Path.GetFileName(fullPath);
        AcceptCommand = new AsyncRelayCommand(() => accept(this));
        RejectCommand = new AsyncRelayCommand(() => reject(this), () => CanRevert);
    }

    /// <summary>
    /// Gets the full path.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; }

    /// <summary>Content before the agent's first write, or null when the agent created the file.</summary>
    public string? OriginalText { get; private set; }

    /// <summary>
    /// Gets a value indicating whether is new.
    /// </summary>
    public bool IsNew => OriginalText is null;

    /// <summary>The tool call whose notification created this row, or null when a client-side file
    /// write did. Only that call's later notifications may correct the snapshot it took.</summary>
    internal string? CreatedByToolCallId { get; }

    /// <summary>Corrects a snapshot taken after the agent's own write already landed (see
    /// ChatViewModel.TrackChangeBeforeWriteAsync): the row already existed by the time the diff that
    /// could prove that arrived, so the wrong snapshot was never replaced. Plain assignment, no
    /// change notification: nothing binds to the text itself, and the caller reads it back at once
    /// to decide revertability, so it must not be posted.</summary>
    internal void CorrectOriginalSnapshot(string original) => OriginalText = original;

    /// <summary>False once <see cref="OriginalText"/> is known not to be the pre-edit content (the
    /// snapshot raced the agent's own write): a revert would only write the edit back over itself
    /// and report success. Never returns to true.</summary>
    public bool CanRevert => _canRevert;

    /// <summary>Clears <see cref="CanRevert"/> with no notification, reporting whether this call is
    /// the one that cleared it. The correction that forces the downgrade runs off the UI thread
    /// while it holds the ledger lock, and the verdict has to become false with it: a Reject that
    /// read the two apart saw the corrected snapshot behind a stale "yes" and wrote that snapshot
    /// back. Only the notification may be posted, and that is what the pair exists for.</summary>
    internal bool TryMarkNotRevertable()
    {
        if (!_canRevert) return false;
        _canRevert = false;
        return true;
    }

    /// <summary>Announces a completed <see cref="TryMarkNotRevertable"/>. UI thread only: it
    /// re-evaluates a command's CanExecute.</summary>
    internal void NotifyRevertabilityChanged()
    {
        OnPropertyChanged(nameof(CanRevert));
        RejectCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Marks the current state as not revertable and raises the revertability changed notification if the state actually changed.
    /// </summary>
    internal void MarkNotRevertable()
    {
        if (TryMarkNotRevertable()) NotifyRevertabilityChanged();
    }

    /// <summary>
    /// Gets or sets the added lines.
    /// </summary>
    public int AddedLines
    {
        get => _addedLines;
        private set => SetProperty(ref _addedLines, value);
    }

    /// <summary>
    /// Gets or sets the removed lines.
    /// </summary>
    public int RemovedLines
    {
        get => _removedLines;
        private set => SetProperty(ref _removedLines, value);
    }

    /// <summary>
    /// Gets the accept command.
    /// </summary>
    public IAsyncRelayCommand AcceptCommand { get; }

    /// <summary>
    /// Gets the reject command.
    /// </summary>
    public IAsyncRelayCommand RejectCommand { get; }

    /// <summary>
    /// Recalculates the added and removed line counts by comparing the original text against the provided current text, storing the results in the AddedLines and RemovedLines properties.
    /// </summary>
    /// <param name="currentText">The current text.</param>
    public void UpdateCounts(string? currentText)
    {
        var (added, removed) = CountLineChanges(OriginalText, currentText ?? string.Empty);
        AddedLines = added;
        RemovedLines = removed;
    }

    // Multiset line diff: cheap, order-insensitive, and good enough for a "+12 −3" badge.
    /// <summary>
    /// Calculates the number of lines added and removed between the original and current text using an order-insensitive multiset comparison of line occurrences.
    /// </summary>
    /// <param name="originalText">The original text.</param>
    /// <param name="currentText">The current text.</param>
    /// <returns>The (int added, int removed) result.</returns>
    private static (int Added, int Removed) CountLineChanges(string? originalText, string currentText)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        if (originalText is not null)
        {
            foreach (var line in SplitLines(originalText))
                remaining[line] = remaining.TryGetValue(line, out var count) ? count + 1 : 1;
        }

        var added = 0;
        foreach (var line in SplitLines(currentText))
        {
            if (remaining.TryGetValue(line, out var count) && count > 0) remaining[line] = count - 1;
            else added++;
        }

        var removed = 0;
        foreach (var count in remaining.Values) removed += count;
        return (added, removed);
    }

    // An empty file has no lines at all; string.Split would report one empty line and inflate the badge.
    // Likewise, a file ending in a standard newline must not count the trailing empty segment as an extra line.
    /// <summary>
    /// Splits the specified text into its individual lines, returning no lines for null or empty input and ignoring a single trailing newline so that it is not counted as an extra empty line.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>A collection of ienumerable items.</returns>
    private static IEnumerable<string> SplitLines(string? text)
    {
        if (text is null || text.Length == 0) yield break;
        if (text.EndsWith("\n", StringComparison.Ordinal))
        {
            text = text.EndsWith("\r\n", StringComparison.Ordinal)
                ? text.Substring(0, text.Length - 2)
                : text.Substring(0, text.Length - 1);
        }

        if (text.Length == 0) yield break;

        foreach (var line in text.Split('\n')) yield return line.TrimEnd('\r');
    }
}
