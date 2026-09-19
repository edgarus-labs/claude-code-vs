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
    public void StripFenceWrapper_TwoSeparateFencedBlocks_LeavesUnchanged()
    {
        // Starts and ends with a fence, but the trailing fence closes the *second* block, not the
        // leading one. Stripping here would delete the first block's opener and the last closer,
        // silently removing agent-printed lines from output the user reviews.
        const string text = "```js\ncode one\n```\nmiddle\n```js\ncode two\n```";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_ContentContainsFenceMidPayload_LeavesUnchanged()
    {
        // Deliberately conservative: an inner "```" may be the real close of the leading fence, so
        // the wrapper is not provably the whole payload. Showing the markers beats deleting a line.
        const string text = "```console\nprint('```')\n```";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_FourBacktickWrapper_StripsBothFences()
    {
        var result = ToolCallContentViewModel.StripFenceWrapper("````console\nfoo\n````");

        Assert.Equal("foo", result);
    }

    [Fact]
    public void StripFenceWrapper_ClosingFenceShorterThanOpening_LeavesUnchanged()
    {
        // A 4-backtick block is not closed by 3 backticks: the trailing "```" is content.
        const string text = "````console\nfoo\n```";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_ClosingFenceLongerThanOpening_LeavesNoStrayBacktick()
    {
        var result = ToolCallContentViewModel.StripFenceWrapper("```console\nfoo\n````");

        Assert.Equal("foo", result);
    }

    [Fact]
    public void StripFenceWrapper_ClosingFenceNotOnItsOwnLine_LeavesUnchanged()
    {
        const string text = "```js\nfoo```";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_UnterminatedFence_LeavesUnchanged()
    {
        const string text = "```console\nfoo";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_TildeFence_LeavesUnchanged()
    {
        // Only backtick wrappers are recognised; a tilde fence is left as the tool printed it.
        const string text = "~~~console\nfoo\n~~~";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_LeadingWhitespaceBeforeFence_LeavesUnchanged()
    {
        const string text = "  ```console\nfoo\n```";

        var result = ToolCallContentViewModel.StripFenceWrapper(text);

        Assert.Same(text, result);
    }

    [Fact]
    public void StripFenceWrapper_TrailingWhitespaceAfterClosingFence_StillStrips()
    {
        var result = ToolCallContentViewModel.StripFenceWrapper("```console\nfoo\n```  \n\n");

        Assert.Equal("foo", result);
    }

    [Fact]
    public void StripFenceWrapper_InfoStringWithBacktick_LeavesUnchanged()
    {
        // CommonMark forbids a backtick in a backtick fence's info string, so this is not a fence.
        const string text = "```con`sole\nfoo\n```";

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
