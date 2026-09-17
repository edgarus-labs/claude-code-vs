using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;

namespace ClaudeCode.Core.ViewModels;

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

    public void Apply(ToolCallUpdate call)
    {
        if (!string.IsNullOrEmpty(call.Title))
        {
            Title = call.Title;
        }

        if (call.Status != ToolCallStatus.Pending || Status == ToolCallStatus.Pending)
        {
            Status = call.Status;
        }

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
