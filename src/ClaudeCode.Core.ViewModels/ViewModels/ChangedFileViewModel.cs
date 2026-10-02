using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels;

/// <summary>One workspace file the agent has written during the current session, with its content
/// before the first write so the change can be rejected (restored) or accepted (kept).</summary>
public sealed class ChangedFileViewModel : ObservableObject
{
    private int _addedLines;
    private int _removedLines;
    private bool _canRevert = true;

    public ChangedFileViewModel(string fullPath, string? originalText, Func<ChangedFileViewModel, Task> accept, Func<ChangedFileViewModel, Task> reject, string? createdByToolCallId)
    {
        FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
        OriginalText = originalText;
        CreatedByToolCallId = createdByToolCallId;
        Name = Path.GetFileName(fullPath);
        AcceptCommand = new AsyncRelayCommand(() => accept(this));
        RejectCommand = new AsyncRelayCommand(() => reject(this), () => CanRevert);
    }

    public string FullPath { get; }

    public string Name { get; }

    /// <summary>Content before the agent's first write, or null when the agent created the file.</summary>
    public string? OriginalText { get; private set; }

    public bool IsNew => OriginalText is null;

    internal string? CreatedByToolCallId { get; }

    internal void CorrectOriginalSnapshot(string original) => OriginalText = original;

    /// <summary>False once <see cref="OriginalText"/> is known not to be the pre-edit content. Never
    /// returns to true.</summary>
    public bool CanRevert => _canRevert;

    internal bool TryMarkNotRevertable()
    {
        if (!_canRevert) return false;
        _canRevert = false;
        return true;
    }

    internal void NotifyRevertabilityChanged()
    {
        OnPropertyChanged(nameof(CanRevert));
        RejectCommand.NotifyCanExecuteChanged();
    }

    internal void MarkNotRevertable()
    {
        if (TryMarkNotRevertable()) NotifyRevertabilityChanged();
    }

    public int AddedLines
    {
        get => _addedLines;
        private set => SetProperty(ref _addedLines, value);
    }

    public int RemovedLines
    {
        get => _removedLines;
        private set => SetProperty(ref _removedLines, value);
    }

    public IAsyncRelayCommand AcceptCommand { get; }

    public IAsyncRelayCommand RejectCommand { get; }

    public void UpdateCounts(string? currentText)
    {
        var (added, removed) = CountLineChanges(OriginalText, currentText ?? string.Empty);
        AddedLines = added;
        RemovedLines = removed;
    }

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
