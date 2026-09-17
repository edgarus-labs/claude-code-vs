using System.Collections.ObjectModel;
using System.Linq;
using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCode.Core.ViewModels
{
    /// <summary>Collapsible card for one tool call, upserted in place as `tool_call`/`tool_call_update` arrive.</summary>
    public sealed class ToolCallCardViewModel : ObservableObject
    {
        public ToolCallCardViewModel(ToolCallUpdate call)
        {
            ToolCallId = call.ToolCallId;
            Apply(call);
        }

        public string ToolCallId { get; }

        private string _title = string.Empty;

        public string Title
        {
            get => _title;
            private set => SetProperty(ref _title, value);
        }

        private ToolCallStatus _status;

        public ToolCallStatus Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        private bool _isExpanded;

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public ObservableCollection<ToolCallContentViewModel> Content { get; } = new ObservableCollection<ToolCallContentViewModel>();

        /// <summary>
        /// Applies a `tool_call`/`tool_call_update` snapshot in place. <see cref="ToolCallUpdate"/> carries no
        /// "field was set" flags, so a partial `tool_call_update` (e.g. a status-only patch) materializes
        /// omitted fields as this type's defaults - empty Title, empty Content, Pending Status (contract gap
        /// confirmed by ClaudeCode.Acp). Treat an empty Title/Content on a later update as "unchanged" rather
        /// than blanking out what's already rendered; Status has no such signal and is always applied.
        /// </summary>
        public void Apply(ToolCallUpdate call)
        {
            if (!string.IsNullOrEmpty(call.Title))
            {
                Title = call.Title;
            }

            Status = call.Status;

            if (call.Content.Count > 0)
            {
                Content.Clear();
                foreach (var contentItem in call.Content)
                {
                    Content.Add(new ToolCallContentViewModel(contentItem));
                }
            }

            if (Status == ToolCallStatus.InProgress && Content.Any())
            {
                IsExpanded = true;
            }
        }
    }
}
