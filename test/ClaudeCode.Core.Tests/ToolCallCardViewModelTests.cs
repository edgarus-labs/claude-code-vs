using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ToolCallCardViewModelTests
{
    [Fact]
    public void Apply_PartialStatusOnlyUpdate_PreservesPreviousTitleAndContent()
    {
        var initial = new ToolCallUpdate
        {
            ToolCallId = "tc-1",
            Title = "Edit file.txt",
            Status = ToolCallStatus.InProgress,
            Content = new[] { new ToolCallContent { Text = "some output" } },
        };
        var card = new ToolCallCardViewModel(initial);

        // ACP tool_call_update payloads may omit fields; the contract's DTO then materializes them as
        // defaults (empty Title, empty Content) rather than "unchanged". A status-only patch must not
        // blank out what's already rendered.
        var partialUpdate = new ToolCallUpdate
        {
            ToolCallId = "tc-1",
            Title = string.Empty,
            Status = ToolCallStatus.Completed,
            Content = System.Array.Empty<ToolCallContent>(),
        };
        card.Apply(partialUpdate);

        Assert.Equal("Edit file.txt", card.Title);
        Assert.Equal(ToolCallStatus.Completed, card.Status);
        Assert.Single(card.Content);
    }

    [Fact]
    public void Apply_ContentOnlyUpdate_DoesNotRegressCompletedStatus()
    {
        var initial = new ToolCallUpdate
        {
            ToolCallId = "tc-1",
            Title = "Edit file.txt",
            Status = ToolCallStatus.Completed,
            Content = new[] { new ToolCallContent { Text = "done" } },
        };
        var card = new ToolCallCardViewModel(initial);

        // A partial update carrying only new content (no explicit status change from the agent)
        // materializes Status as the enum default (Pending). That must not regress a terminal status.
        var contentOnlyUpdate = new ToolCallUpdate
        {
            ToolCallId = "tc-1",
            Content = new[] { new ToolCallContent { Text = "more output" } },
        };
        card.Apply(contentOnlyUpdate);

        Assert.Equal(ToolCallStatus.Completed, card.Status);
        Assert.Single(card.Content);
    }
}
