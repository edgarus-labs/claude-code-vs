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

    public ChangedFileViewModel(string fullPath, string? originalText, Func<ChangedFileViewModel, Task> accept, Func<ChangedFileViewModel, Task> reject)
    {
        FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
        OriginalText = originalText;
        Name = Path.GetFileName(fullPath);
        Directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        AcceptCommand = new AsyncRelayCommand(() => accept(this));
        RejectCommand = new AsyncRelayCommand(() => reject(this));
    }

    public string FullPath { get; }

    public string Name { get; }

    public string Directory { get; }

    /// <summary>Content before the agent's first write, or null when the agent created the file.</summary>
    public string? OriginalText { get; }

    public bool IsNew => OriginalText is null;

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

    public void UpdateCounts(string currentText)
    {
        var (added, removed) = CountLineChanges(OriginalText, currentText);
        AddedLines = added;
        RemovedLines = removed;
    }

    // Multiset line diff: cheap, order-insensitive, and good enough for a "+12 −3" badge.
    internal static (int Added, int Removed) CountLineChanges(string? originalText, string currentText)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        if (originalText is not null)
        {
            foreach (var line in SplitLines(originalText))
                remaining[line] = remaining.TryGetValue(line, out var count) ? count + 1 : 1;
        }

        var added = 0;
        foreach (var line in SplitLines(currentText ?? string.Empty))
        {
            if (remaining.TryGetValue(line, out var count) && count > 0) remaining[line] = count - 1;
            else added++;
        }

        var removed = 0;
        foreach (var count in remaining.Values) removed += count;
        return (added, removed);
    }

    // An empty file has no lines at all; string.Split would report one empty line and inflate the badge.
    private static IEnumerable<string> SplitLines(string? text)
    {
        if (text is null || text.Length == 0) yield break;

        foreach (var line in text.Split('\n')) yield return line.TrimEnd('\r');
    }
}
