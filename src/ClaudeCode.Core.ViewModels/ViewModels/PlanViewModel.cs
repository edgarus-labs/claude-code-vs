using ClaudeCode.Contracts;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ClaudeCode.Core.ViewModels;

public sealed class PlanViewModel
{
    public PlanViewModel(IReadOnlyList<PlanEntry> entries)
    {
        Entries = new ObservableCollection<PlanEntry>(entries);
    }

    public ObservableCollection<PlanEntry> Entries { get; }
}
