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

    public ObservableCollection<PlanEntry> Entries { get; }

    public int CompletedCount { get; }

    public int TotalCount => Entries.Count;

    public string ProgressLabel => CompletedCount + "/" + TotalCount;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}
