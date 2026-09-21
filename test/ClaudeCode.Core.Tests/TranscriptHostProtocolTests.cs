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
    // A trailing-dot host is the fully-qualified spelling of the same name but a *different* string
    // to Chromium, so it can address a document this host never served. .NET keeps the dot in
    // Uri.Host, so the ordinal equality below rejects it - pin that, it is load-bearing hardening.
    [InlineData("https://claudecode.transcript./index.html")]
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

    // Every FontSize/Width/Height in the chat views was authored against DefaultFontSize and is now
    // bound through this scale, so the default must be an exact identity - a design value that drifts
    // to 11.99 at the untouched default would repaint the whole panel off-spec.
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

    // Streamed text and tool-call progress mutate these view models in place; a turn driven from
    // claude.ai/code never sets IsBusy, so these are the only signals that it changed.
    [Theory]
    [InlineData(nameof(ChatMessageViewModel.Text))]
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

    // RemoteControlUrl is agent-reported, so it crosses the untrusted boundary and is then handed
    // to ShellExecute. Anything but an absolute https URL must be refused: http would let a hostile
    // agent point the Remote Control pill at a plaintext endpoint, and a non-web scheme would hand
    // an arbitrary shell verb to the OS.
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

    // If the maximum wait were not longer than the coalescing window, every streamed chunk would
    // paint synchronously and the coalescing that exists to stop O(n^2) re-renders on session
    // resume would never collapse anything.
    [Fact]
    public void RenderCoalesceWindow_IsShorterThanTheMaximumWait()
    {
        Assert.True(TranscriptHostProtocol.RenderCoalesceWindow < TranscriptHostProtocol.MaxRenderInterval);
    }

    // A turn driven from claude.ai/code never sets IsBusy, so the host's one-second activity timer
    // never starts and this rule is the only thing that can repaint it: ChatPanelView's
    // ScheduleTranscriptRender consults it on every change and paints outright when it says so
    // instead of restarting the coalescing timer. The boundary is inclusive - a page exactly
    // MaxRenderInterval stale paints now rather than joining another coalescing window.
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
