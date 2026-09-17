using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests
{
    public class ToolCallCardViewModelTests
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
    }
}
