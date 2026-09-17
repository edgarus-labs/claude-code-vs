using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChatTranscriptScrollPolicyTests
{
    [Fact]
    public void ShouldAutoScroll_WasAtBottomAndExtentGrew_ReturnsTrue()
    {
        Assert.True(ChatTranscriptScrollPolicy.ShouldAutoScroll(wasAtBottom: true, extentHeightChange: 10));
    }

    [Fact]
    public void ShouldAutoScroll_WasAtBottomButExtentDidNotGrow_ReturnsFalse()
    {
        Assert.False(ChatTranscriptScrollPolicy.ShouldAutoScroll(wasAtBottom: true, extentHeightChange: 0));
    }

    [Fact]
    public void ShouldAutoScroll_WasScrolledUp_NeverAutoScrollsEvenWhenContentGrows()
    {
        Assert.False(ChatTranscriptScrollPolicy.ShouldAutoScroll(wasAtBottom: false, extentHeightChange: 50));
    }
}
