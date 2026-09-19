using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ToolCallContentViewModelTests
{
    [Fact]
    public void StripFenceWrapper_TextWrappedInFence_StripsMarkersAndLanguage()
    {
        var result = ToolCallContentViewModel.StripFenceWrapper("```console\n(Bash completed with no output)\n```");

        Assert.Equal("(Bash completed with no output)", result);
    }

    [Fact]
    public void StripFenceWrapper_MultiLineContent_KeepsInnerLines()
    {
        var result = ToolCallContentViewModel.StripFenceWrapper("```console\nline one\nline two\n```");

        Assert.Equal("line one\nline two", result);
    }

    [Fact]
    public void StripFenceWrapper_CrlfTerminatedContent_LeavesNoTrailingCarriageReturn()
    {
        // Tool output on Windows is CRLF-terminated, so the newline before the closing fence is
        // "\r\n" - both characters belong to the wrapper, not to the content.
        var result = ToolCallContentViewModel.StripFenceWrapper("```console\r\nline one\r\nline two\r\n```");

        Assert.Equal("line one\r\nline two", result);
    }

    [Fact]
    public void StripFenceWrapper_EmptyFencedBlock_ReturnsEmptyString()
    {
        var result = ToolCallContentViewModel.StripFenceWrapper("```console\n```");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void StripFenceWrapper_NoFenceWrapper_ReturnsUnchanged()
    {
        const string text = "plain tool output, no fence";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_FenceOnlyPartOfText_LeavesUnchanged()
    {
        // A fence that doesn't span the entire text is presumably real content the tool printed,
        // not a wrapper applied around the whole payload - must not be touched.
        const string text = "before\n```console\nsome code\n```\nafter";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_InfoStringWithSpace_LeavesUnchanged()
    {
        const string text = "```not a real lang\ncontent\n```";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_NullOrEmpty_ReturnsSameValue()
    {
        Assert.Null(ToolCallContentViewModel.StripFenceWrapper(null));
        Assert.Equal(string.Empty, ToolCallContentViewModel.StripFenceWrapper(string.Empty));
    }

    [Fact]
    public void Constructor_ContentTextWrappedInFence_StripsWrapperOnAssignment()
    {
        var content = new ToolCallContent { Text = "```console\nok\n```" };

        var viewModel = new ToolCallContentViewModel(content);

        Assert.Equal("ok", viewModel.Text);
    }
}
