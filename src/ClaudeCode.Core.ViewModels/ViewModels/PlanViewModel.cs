using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ClaudeCode.Core.ViewModels;

public sealed class PlanViewModel : ObservableObject
{
    private bool _isExpanded = true;

    public PlanViewModel(IReadOnlyList<PlanEntry> entries)
    {
        Entries = new ObservableCollection<PlanEntry>(entries);
        CompletedCount = entries.Count(entry => entry.Status == PlanEntryStatus.Completed);
    }

    /// <summary>
    /// Gets the entries.
    /// </summary>
    public ObservableCollection<PlanEntry> Entries { get; }

    /// <summary>
    /// Gets the completed count.
    /// </summary>
    public int CompletedCount { get; }

    /// <summary>
    /// Gets the total count.
    /// </summary>
    public int TotalCount => Entries.Count;

    /// <summary>
    /// Gets the progress label.
    /// </summary>
    public string ProgressLabel => CompletedCount + "/" + TotalCount;

    /// <summary>
    /// Gets or sets a value indicating whether is expanded.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}
