using ClaudeCode.Core.ViewModels;
using System.Linq;
using System.Text;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class DiffBuilderTests
{
    [Fact]
    public void Build_EmptyOldText_MarksEveryNewLineAsAdded()
    {
        var lines = DiffBuilder.Build(string.Empty, "one\ntwo");

        Assert.All(lines, line => Assert.Equal(DiffLineKind.Added, line.Kind));
        Assert.Equal(new[] { "one", "two" }, lines.Select(line => line.Text));
    }

    [Fact]
    public void Build_IdenticalTexts_ProducesOnlyContextLines()
    {
        var lines = DiffBuilder.Build("one\ntwo\nthree", "one\ntwo\nthree");

        Assert.All(lines, line => Assert.Equal(DiffLineKind.Context, line.Kind));
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void Build_TrailingNewlineDifference_IsVisible()
    {
        // A trailing "\n" produces one more (empty) split segment than text without it; that extra
        // segment must render as a real added/removed line, not be silently absorbed.
        var lines = DiffBuilder.Build("one", "one\n");

        Assert.Equal(DiffLineKind.Context, lines[0].Kind);
        var trailing = Assert.Single(lines.Skip(1));
        Assert.Equal(DiffLineKind.Added, trailing.Kind);
        Assert.Equal(string.Empty, trailing.Text);
    }

    [Fact]
    public void Build_AboveAlignmentCellLimit_FallsBackToFullRemoveAdd()
    {
        // _maxAlignmentCells = 2_000_000: the O(n*m) LCS table is skipped once old-lines * new-lines
        // would exceed it, falling back to a flat remove-everything/add-everything rendering.
        var oldText = string.Join("\n", Enumerable.Range(0, 1500).Select(i => "old-" + i));
        var newText = string.Join("\n", Enumerable.Range(0, 1500).Select(i => "new-" + i));

        var lines = DiffBuilder.Build(oldText, newText);

        Assert.Equal(1500, lines.Count(line => line.Kind == DiffLineKind.Removed));
        Assert.Equal(1500, lines.Count(line => line.Kind == DiffLineKind.Added));
        Assert.DoesNotContain(lines, line => line.Kind == DiffLineKind.Context);
    }

    [Fact]
    public void Build_RepeatedCallWithSameTexts_ReturnsCachedResultInstance()
    {
        // M15: rebuilding a tool-call card on every tool_call_update must not recompute an unchanged
        // diff. The single-entry cache is proven by reference equality of the returned list.
        var oldText = "alpha\nbeta";
        var newText = "alpha\ngamma";

        var first = DiffBuilder.Build(oldText, newText);
        var second = DiffBuilder.Build(new StringBuilder(oldText).ToString(), new StringBuilder(newText).ToString());

        Assert.Same(first, second);
    }

    [Fact]
    public void Build_DifferentTextsAfterCache_RecomputesAndReturnsNewInstance()
    {
        var first = DiffBuilder.Build("alpha", "beta");
        var second = DiffBuilder.Build("alpha", "delta");

        Assert.NotSame(first, second);
    }
}
