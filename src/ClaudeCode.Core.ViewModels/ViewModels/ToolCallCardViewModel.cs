using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
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

    /// <summary>
    /// Gets the tool call id.
    /// </summary>
    public string ToolCallId { get; }

    private string _title = string.Empty;

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    private ToolCallStatus _status;

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public ToolCallStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private bool _isExpanded;

    /// <summary>
    /// Gets or sets a value indicating whether is expanded.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Gets the content.
    /// </summary>
    public ObservableCollection<ToolCallContentViewModel> Content { get; } = new ObservableCollection<ToolCallContentViewModel>();

    private string? _cachedDiffOldText;
    private string? _cachedDiffNewText;
    private IReadOnlyList<DiffLineViewModel>? _cachedDiffLines;

    /// <summary>True once any update has named this call a subagent; see <see cref="ToolCallUpdate.IsSubagent"/>.</summary>
    public bool IsSubagent { get; private set; }

    /// <summary>
    /// Updates the tool call view model&apos;s state based on the provided ToolCallUpdate, setting the subagent flag, title, status, content, and expanding the view when the call is in progress.
    /// </summary>
    /// <param name="call">The call.</param>
    public void Apply(ToolCallUpdate call)
    {
        if (call.IsSubagent)
        {
            IsSubagent = true;
        }

        if (!string.IsNullOrEmpty(call.Title))
        {
            Title = ToolDisplayName.Describe(call.Title);
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
                Content.Add(new ToolCallContentViewModel(contentItem, ResolveDiffLines(contentItem)));
            }
        }

        if (Status == ToolCallStatus.InProgress && Content.Any())
        {
            IsExpanded = true;
        }
    }

    private IReadOnlyList<DiffLineViewModel>? ResolveDiffLines(ToolCallContent contentItem)
    {
        if (!contentItem.IsDiff)
        {
            return null;
        }

        var oldText = contentItem.OldText ?? string.Empty;
        var newText = contentItem.NewText ?? string.Empty;
        if (_cachedDiffLines is not null && _cachedDiffOldText == oldText && _cachedDiffNewText == newText)
        {
            return _cachedDiffLines;
        }

        var diffLines = DiffBuilder.Build(oldText, newText);
        _cachedDiffOldText = oldText;
        _cachedDiffNewText = newText;
        _cachedDiffLines = diffLines;
        return diffLines;
    }
}
