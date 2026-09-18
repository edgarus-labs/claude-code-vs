using ClaudeCode.Core.ViewModels;
using System;
using System.Linq;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_EmptySide_DoesNotAllocateAnAlignmentTable(bool emptyOld)
    {
        // Repeated empty lines make output allocation predictable without allocating line strings.
        // The nonempty comparison already takes the bounded full-remove/add path.
        var text = new string('\n', 100_000);
        var fallbackSide = new string('\n', 20);
        _ = DiffBuilder.Build(string.Empty, "warmup");
        _ = DiffBuilder.Build("warmup", string.Empty);

        var beforeFallback = GC.GetAllocatedBytesForCurrentThread();
        _ = DiffBuilder.Build(emptyOld ? fallbackSide : text, emptyOld ? text : fallbackSide);
        var fallbackAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeFallback;

        var beforeEmpty = GC.GetAllocatedBytesForCurrentThread();
        var lines = DiffBuilder.Build(emptyOld ? string.Empty : text, emptyOld ? text : string.Empty);
        var emptyAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeEmpty;

        Assert.Equal(100_001, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Equal(emptyOld ? DiffLineKind.Added : DiffLineKind.Removed, line.Kind);
            Assert.Equal(string.Empty, line.Text);
        });
        Assert.True(emptyAllocation <= fallbackAllocation + 16_384,
            $"Empty side allocated {emptyAllocation} bytes versus {fallbackAllocation} for full-remove/add.");
    }
}
