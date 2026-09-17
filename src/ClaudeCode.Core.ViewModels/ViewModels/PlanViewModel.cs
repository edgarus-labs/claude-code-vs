using System.Collections.Generic;
using System.Collections.ObjectModel;
using ClaudeCode.Contracts;

namespace ClaudeCode.Core.ViewModels
{
    /// <summary>Snapshot of the agent's current plan/todo list, shown as a strip above the composer.</summary>
    public sealed class PlanViewModel
    {
        public PlanViewModel(IReadOnlyList<PlanEntry> entries)
        {
            Entries = new ObservableCollection<PlanEntry>(entries);
        }

        public ObservableCollection<PlanEntry> Entries { get; }
    }
}
