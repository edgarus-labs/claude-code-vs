using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System.Linq;
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

        var contentOnlyUpdate = new ToolCallUpdate
        {
            ToolCallId = "tc-1",
            Content = new[] { new ToolCallContent { Text = "more output" } },
        };
        card.Apply(contentOnlyUpdate);

        Assert.Equal(ToolCallStatus.Completed, card.Status);
        Assert.Single(card.Content);
    }

    [Fact]
    public void Apply_RepeatedDiffAfterAnotherCardBuildsDifferentDiff_ReusesOwnCachedDiffLines()
    {
        var cardA = new ToolCallCardViewModel(new ToolCallUpdate
        {
            ToolCallId = "tc-a",
            Status = ToolCallStatus.InProgress,
            Content = new[] { new ToolCallContent { Path = "a.txt", OldText = "alpha\nbeta", NewText = "alpha\ngamma" } },
        });
        var firstDiff = cardA.Content.Single().DiffLines;

        _ = new ToolCallCardViewModel(new ToolCallUpdate
        {
            ToolCallId = "tc-b",
            Status = ToolCallStatus.InProgress,
            Content = new[] { new ToolCallContent { Path = "b.txt", OldText = "one", NewText = "two" } },
        });

        cardA.Apply(new ToolCallUpdate
        {
            ToolCallId = "tc-a",
            Status = ToolCallStatus.Completed,
            Content = new[] { new ToolCallContent { Path = "a.txt", OldText = "alpha\nbeta", NewText = "alpha\ngamma" } },
        });
        var secondDiff = cardA.Content.Single().DiffLines;

        Assert.Same(firstDiff, secondDiff);
    }
}
