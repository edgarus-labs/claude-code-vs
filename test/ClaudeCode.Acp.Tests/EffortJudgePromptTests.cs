using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class EffortJudgePromptTests
{
    private static string[] LevelNames => Enum.GetNames<EffortLevel>();

    // Auto's ceiling is High: the judge is offered exactly the EffortLevel names, nothing above.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SystemPrompts_OfferExactlyTheEffortLevels(bool retry)
    {
        var prompt = retry ? EffortJudgePrompt.RetrySystemPrompt : EffortJudgePrompt.SystemPrompt;
        foreach (var name in LevelNames) Assert.Contains("`" + name.ToLowerInvariant() + "`", prompt);
        Assert.DoesNotContain("xhigh", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("`max`", prompt, StringComparison.OrdinalIgnoreCase);
        // The state is guarded as untrusted data (issue #49 test 7); the exact sentence is not pinned.
        Assert.Contains("untrusted", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Callers index by ordinal (labels here, the effort values in the view model): each level's
    // lowercase name must parse back to that very level.
    [Fact]
    public void ParseReply_EveryEffortLevelName_ParsesToThatLevel()
    {
        foreach (EffortLevel level in Enum.GetValues<EffortLevel>())
        {
            Assert.Equal(level, EffortJudgePrompt.ParseReply(level.ToString().ToLowerInvariant()));
        }

        Assert.Equal(new[] { 0, 1, 2 }, new[] { EffortLevel.Low, EffortLevel.Medium, EffortLevel.High }.Select(level => (int)level));
    }

    [Fact]
    public void RenderUser_EmbedsThePreprocessedRequestExactlyOnce()
    {
        var rendered = EffortJudgePrompt.RenderUser("\u001b[31mzrób commit i push\u001b[0m");
        Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(rendered, "zrób commit i push"));

        var huge = "HEAD " + new string('x', 50_000) + " TAIL";
        var renderedHuge = EffortJudgePrompt.RenderUser(huge);
        Assert.Equal(1, CountOf(renderedHuge, EffortJudgePrompt.Preprocess(huge)));
        Assert.True(renderedHuge.Length < EffortJudgePrompt.MaxStateChars + 500, renderedHuge.Length.ToString(CultureInfo.InvariantCulture));
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Theory]
    [InlineData("\u001b[1;32mfix\u001b[0m the build", "fix the build")]
    [InlineData("fix\u001b[2K the \u001b[1;1Hbuild", "fix the build")]
    [InlineData("see <tool_result>huge dump</tool_result> and fix it", "see and fix it")]
    [InlineData("revert 0123456789abcdef0123456789abcdef01234567 please", "revert 0123456 please")]
    // Stripping must not leave (almost) nothing: a message that is only a code block stays.
    [InlineData("```\nls\n```", "```\nls\n```")]
    // ... nor when the whole message, or all but a short trailer, is one tag block.
    [InlineData("<task>Design the API for the new plugin system</task>", "<task>Design the API for the new plugin system</task>")]
    [InlineData("<task>Design the API for the new plugin system</task> ok go now please", "<task>Design the API for the new plugin system</task> ok go now please")]
    [InlineData("Fix <div className=\"x\">the layout of this thing</div> now please", "Fix <div className=\"x\">the layout of this thing</div> now please")]
    public void Preprocess_StripsNoiseTinyJudgesCopy(string raw, string expected)
    {
        Assert.Equal(expected, EffortJudgePrompt.Preprocess(raw));
    }

    // The fence and its code go; the collapsed remainder is not pinned (a lone space may survive).
    [Theory]
    [InlineData("explain this:\n```cs\nvar x = 1;\n```\nshort")]
    [InlineData("explain this:\r\n```cs\r\nvar x = 1;\r\n```\r\nshort")]
    public void Preprocess_DropsFencedCode_KeepsTheProse(string raw)
    {
        var normalized = Regex.Replace(EffortJudgePrompt.Preprocess(raw), @"\s+", " ").Trim();
        Assert.Equal("explain this: short", normalized);
    }

    [Fact]
    public void Preprocess_LongMessage_KeepsBothEndsWithinTheBound()
    {
        var message = "HEAD " + new string('x', 5000) + " TAIL";
        var result = EffortJudgePrompt.Preprocess(message);
        Assert.Equal(EffortJudgePrompt.MaxStateChars, result.Length);
        Assert.StartsWith("HEAD ", result);
        Assert.EndsWith(" TAIL", result);
        int markerStart = result.IndexOf("\n[… ", StringComparison.Ordinal);
        int markerEnd = result.IndexOf(" chars omitted …]\n", StringComparison.Ordinal) + " chars omitted …]\n".Length;
        int omitted = int.Parse(result.AsSpan(markerStart + 4, result.IndexOf(' ', markerStart + 4) - markerStart - 4), CultureInfo.InvariantCulture);
        Assert.Equal(message.Length - (result.Length - (markerEnd - markerStart)), omitted);
    }

    // Around the omitted count's digit boundaries the marker widens; the result must still fit.
    [Fact]
    public void Preprocess_EveryLength_StaysWithinTheBound()
    {
        for (int length = EffortJudgePrompt.MaxStateChars + 1; length <= 13_000; length++)
        {
            int actual = EffortJudgePrompt.Preprocess(new string('x', length)).Length;
            Assert.True(actual <= EffortJudgePrompt.MaxStateChars, $"{length} chars preprocessed to {actual}");
        }
    }

    // Cutting inside a surrogate pair would leave a lone half, sent to the judge as U+FFFD.
    [Fact]
    public void Preprocess_Truncation_NeverSplitsASurrogatePair()
    {
        for (int emoji = 1000; emoji < 1100; emoji++)
        {
            var result = EffortJudgePrompt.Preprocess(string.Concat(Enumerable.Repeat("😀", emoji)));
            for (int i = 0; i < result.Length; i++)
            {
                if (char.IsHighSurrogate(result[i])) Assert.True(i + 1 < result.Length && char.IsLowSurrogate(result[++i]), $"lone high surrogate at {i} ({emoji} emoji)");
                else Assert.False(char.IsLowSurrogate(result[i]), $"lone low surrogate at {i} ({emoji} emoji)");
            }
        }
    }

    // The message is untrusted: pasted logs full of unclosed tags must not make the envelope pattern
    // rescan the rest of the text once per tag. The input is bounded before the patterns run (without
    // that bound this input costs many seconds); the time limit is generous and catches an
    // order-of-magnitude regression. The patterns' own match timeouts are a second guard that this
    // input, already bounded, does not reach.
    [Theory]
    [InlineData("<a>")]
    [InlineData("<a ")]
    [InlineData("```")]
    [InlineData("```x\n")]
    public void Preprocess_UnclosedOpeners_StayBoundedAndFinish(string unit)
    {
        var message = string.Concat(Enumerable.Repeat(unit, 100_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = EffortJudgePrompt.Preprocess(message);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.True(result.Length <= EffortJudgePrompt.MaxStateChars);
        Assert.True(result.Length > 0);
    }

    // A fence that never closes is not a code block: everything after it is still the user's text,
    // and the request usually sits at the end. The head and tail are kept, as for any long message.
    [Fact]
    public void Preprocess_UnclosedFence_KeepsTheTextAfterIt()
    {
        var message = "Please look at this code:\n```\n" + string.Concat(Enumerable.Repeat("var x = 1;\n", 400)) + "now redesign the retry API";

        var result = EffortJudgePrompt.Preprocess(message);

        Assert.StartsWith("Please look at this code:", result);
        Assert.EndsWith("now redesign the retry API", result);
    }

    // Closed fences still go, and an unclosed one after them does not take the rest with it.
    [Fact]
    public void Preprocess_ClosedFenceGoes_AnUnclosedOneAfterItStays()
    {
        var result = EffortJudgePrompt.Preprocess("explain this:\n```cs\nvar x = 1;\n```\nthen fix that:\n```\nbroken(");

        Assert.DoesNotContain("var x = 1;", result);
        Assert.Contains("then fix that:", result);
        Assert.Contains("broken(", result);
    }

    // The pre-clean cut (16,000 chars) keeps both ends, like the final one.
    [Theory]
    [InlineData(15_990)]
    [InlineData(16_000)]
    [InlineData(16_010)]
    [InlineData(60_000)]
    public void Preprocess_LengthsAroundThePreCleanBound_KeepBothEndsWithinTheBound(int filler)
    {
        var result = EffortJudgePrompt.Preprocess("HEAD " + new string('x', filler) + " TAIL");

        Assert.True(result.Length <= EffortJudgePrompt.MaxStateChars, result.Length.ToString(CultureInfo.InvariantCulture));
        Assert.StartsWith("HEAD ", result);
        Assert.EndsWith(" TAIL", result);
    }

    [Theory]
    [InlineData("`low`\n\nThe request specifies exactly what to do.", EffortLevel.Low)]
    [InlineData("HIGH", EffortLevel.High)]
    [InlineData("medium - not high", EffortLevel.Medium)]
    [InlineData("highly likely: medium", EffortLevel.Medium)]
    [InlineData("I'd say `high`.", EffortLevel.High)]
    public void ParseReply_TakesTheEarliestWholeWordLabel(string reply, EffortLevel expected)
    {
        Assert.Equal(expected, EffortJudgePrompt.ParseReply(reply));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nie mam kontekstu do wykonania tego polecenia.")]
    [InlineData("lowest highest")]
    public void ParseReply_NoLabel_IsNull(string reply)
    {
        Assert.Null(EffortJudgePrompt.ParseReply(reply));
    }
}
