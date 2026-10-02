using ClaudeCode.Core.ViewModels;
using System;
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

    [Theory]
    [InlineData("file:///C:/Users/me/notes.txt")]
    [InlineData("http://claudecode.transcript/index.html")]
    [InlineData("https://claudecode.transcript:8443/index.html")]
    [InlineData("https://user:pw@claudecode.transcript/index.html")]
    [InlineData("about:blank")]
    [InlineData("index.html")]
    [InlineData("")]
    [InlineData("https://claudecode.transcript./index.html")]
    [InlineData(null)]
    public void IsTranscriptOrigin_AnythingButTheTranscriptOrigin_IsRejected(string? uri)
    {
        Assert.False(TranscriptHostProtocol.IsTranscriptOrigin(uri));
    }

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

    [Fact]
    public void ScaleFontSize_AtTheDefault_ReturnsTheDesignValueUnchanged()
    {
        Assert.Equal(12d, TranscriptHostProtocol.ScaleFontSize(TranscriptHostProtocol.DefaultFontSize, 12d));
        Assert.Equal(24d, TranscriptHostProtocol.ScaleFontSize(TranscriptHostProtocol.DefaultFontSize, 24d));
    }

    [Fact]
    public void ScaleFontSize_AwayFromTheDefault_KeepsTheDesignProportion()
    {
        Assert.Equal(24d, TranscriptHostProtocol.ScaleFontSize(26d, 12d));
        Assert.Equal(10d / 13d * TranscriptHostProtocol.MinFontSize,
            TranscriptHostProtocol.ScaleFontSize(TranscriptHostProtocol.MinFontSize, 10d));
    }

    [Theory]
    [InlineData(nameof(ChatMessageViewModel.Text))]
    [InlineData(nameof(ChatMessageViewModel.ThinkingVersion))]
    [InlineData(nameof(ChatMessageViewModel.DurationSeconds))]
    [InlineData(nameof(ChatMessageViewModel.TokensUsed))]
    [InlineData(nameof(ToolCallCardViewModel.Title))]
    [InlineData(nameof(ChatMessageViewModel.Images))]
    [InlineData(nameof(ChatMessageViewModel.IsPending))]
    [InlineData(nameof(ToolCallCardViewModel.Status))]
    public void AffectsTranscript_PropertyCarriedByThePayload_RequiresRepaint(string propertyName)
    {
        Assert.True(TranscriptHostProtocol.AffectsTranscript(propertyName));
    }

    [Theory]
    [InlineData(nameof(ToolCallCardViewModel.IsExpanded))]
    [InlineData("ToolCallId")]
    [InlineData("")]
    [InlineData(null)]
    public void AffectsTranscript_PropertyNotCarriedByThePayload_RequiresNoRepaint(string? propertyName)
    {
        Assert.False(TranscriptHostProtocol.AffectsTranscript(propertyName));
    }

    [Theory]
    [InlineData("http://claude.ai/code/abc")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("/code/abc")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeRemoteControlLink_AnythingButAbsoluteHttps_IsRefused(string? url)
    {
        Assert.Null(TranscriptHostProtocol.NormalizeRemoteControlLink(url));
    }

    [Fact]
    public void NormalizeRemoteControlLink_AbsoluteHttps_IsReturnedAsAnAbsoluteUri()
    {
        Assert.Equal(
            "https://claude.ai/code/abc",
            TranscriptHostProtocol.NormalizeRemoteControlLink("https://claude.ai/code/abc"));
    }

    [Fact]
    public void RenderCoalesceWindow_IsShorterThanTheMaximumWait()
    {
        Assert.True(TranscriptHostProtocol.RenderCoalesceWindow < TranscriptHostProtocol.MaxRenderInterval);
    }

    [Fact]
    public void ShouldPaintImmediately_StaleForExactlyTheMaximumWait_PaintsNow()
    {
        Assert.True(TranscriptHostProtocol.ShouldPaintImmediately(TranscriptHostProtocol.MaxRenderInterval));
    }

    [Fact]
    public void ShouldPaintImmediately_OneTickShortOfTheMaximumWait_Coalesces()
    {
        Assert.False(TranscriptHostProtocol.ShouldPaintImmediately(
            TranscriptHostProtocol.MaxRenderInterval - TimeSpan.FromTicks(1)));
    }
}
