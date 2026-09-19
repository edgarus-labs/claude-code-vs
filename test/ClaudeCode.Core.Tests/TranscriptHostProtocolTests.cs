using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class TranscriptHostProtocolTests
{
    [Fact]
    public void IsTranscriptOrigin_TranscriptPage_IsAccepted()
    {
        Assert.True(TranscriptHostProtocol.IsTranscriptOrigin(TranscriptHostProtocol.PageUrl));
    }

    [Fact]
    public void IsTranscriptOrigin_TranscriptOriginRoot_IsAccepted()
    {
        Assert.True(TranscriptHostProtocol.IsTranscriptOrigin("https://ClaudeCode.Transcript/"));
    }

    // Dropping a file from Explorer/Solution Explorer onto the transcript navigates the frame to
    // file://; accepting that origin is what permanently bricked the transcript for the session.
    [Theory]
    [InlineData("file:///C:/Users/me/notes.txt")]
    [InlineData("http://claudecode.transcript/index.html")]
    [InlineData("https://claudecode.transcript:8443/index.html")]
    [InlineData("https://user:pw@claudecode.transcript/index.html")]
    [InlineData("about:blank")]
    [InlineData("index.html")]
    [InlineData("")]
    [InlineData(null)]
    public void IsTranscriptOrigin_AnythingButTheTranscriptOrigin_IsRejected(string? uri)
    {
        Assert.False(TranscriptHostProtocol.IsTranscriptOrigin(uri));
    }

    // A prefix match against "https://claudecode.transcript" (no trailing separator) would accept
    // this, letting a foreign document receive the whole conversation and drive the host.
    [Fact]
    public void IsTranscriptOrigin_LookAlikeHost_IsRejected()
    {
        Assert.False(TranscriptHostProtocol.IsTranscriptOrigin("https://claudecode.transcript.example.com/index.html"));
    }

    [Fact]
    public void StepFontSize_AtUpperBound_StaysClamped()
    {
        Assert.Equal(TranscriptHostProtocol.MaxFontSize,
            TranscriptHostProtocol.StepFontSize(TranscriptHostProtocol.MaxFontSize, 1));
    }

    [Fact]
    public void StepFontSize_AtLowerBound_StaysClamped()
    {
        Assert.Equal(TranscriptHostProtocol.MinFontSize,
            TranscriptHostProtocol.StepFontSize(TranscriptHostProtocol.MinFontSize, -1));
    }

    // Only the sign of the delta matters: a trackpad reporting 120 must not jump 120 sizes.
    [Fact]
    public void StepFontSize_LargeDelta_MovesOneStep()
    {
        Assert.Equal(14d, TranscriptHostProtocol.StepFontSize(13d, 120d));
        Assert.Equal(12d, TranscriptHostProtocol.StepFontSize(13d, -120d));
    }

    [Fact]
    public void StepFontSize_ZeroDelta_DoesNotMove()
    {
        Assert.Equal(13d, TranscriptHostProtocol.StepFontSize(13d, 0d));
    }

    // Streamed text and tool-call progress mutate these view models in place; a turn driven from
    // claude.ai/code never sets IsBusy, so these are the only signals that it changed.
    [Theory]
    [InlineData(nameof(ChatMessageViewModel.Text))]
    [InlineData(nameof(ChatMessageViewModel.DurationSeconds))]
    [InlineData(nameof(ChatMessageViewModel.TokensUsed))]
    [InlineData(nameof(ToolCallCardViewModel.Title))]
    [InlineData(nameof(ToolCallCardViewModel.Status))]
    public void AffectsTranscript_PropertyCarriedByThePayload_RequiresRepaint(string propertyName)
    {
        Assert.True(TranscriptHostProtocol.AffectsTranscript(propertyName));
    }

    // IsExpanded is WPF-only state the page manages itself; repainting for it would re-push the
    // whole conversation (every attached image included) for no visible change.
    [Theory]
    [InlineData(nameof(ToolCallCardViewModel.IsExpanded))]
    [InlineData("ToolCallId")]
    [InlineData("")]
    [InlineData(null)]
    public void AffectsTranscript_PropertyNotCarriedByThePayload_RequiresNoRepaint(string? propertyName)
    {
        Assert.False(TranscriptHostProtocol.AffectsTranscript(propertyName));
    }
}
