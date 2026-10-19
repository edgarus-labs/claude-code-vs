using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Represents a view model for displaying tool call card details including identifier, title, status, expansion state, and content collection.
/// </summary>
public sealed class ToolCallCardViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the ToolCallCardViewModel class with the specified ToolCallUpdate, setting the ToolCallId and applying the update details.
    /// </summary>
    /// <param name="call">The call.</param>
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
    /// Applies the specified tool call update by updating the subagent status, title, status, and content, and expands the tool call if it is in progress and has content.
    /// </summary>
    /// <param name="call">The call.</param>
    public void Apply(ToolCallUpdate call)
    {
        if (call.IsSubagent) IsSubagent = true;

        if (!string.IsNullOrEmpty(call.Title))
        {
            // MCP tools arrive as a routing identifier (mcp__visual-studio__listAppWindows).
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

    // A tool call card is rebuilt (new ToolCallContentViewModel per item) on every tool_call_update,
    // even when the diff content itself is unchanged. This single-entry cache is scoped to this
    // card instance (one card per ToolCallId) so repeated updates on the same tool call reuse the
    // previously computed diff without recomputing it, and without leaking state to other cards.
    /// <summary>
    /// Computes and caches the diff lines between old and new text content, returning null if the content is not a diff.
    /// </summary>
    /// <param name="contentItem">The content item.</param>
    /// <returns>A collection of iread only list items.</returns>
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
