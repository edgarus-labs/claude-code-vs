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
    public void Build_CreatedFileEndingInNewline_CountsOnlyItsRealLines()
    {
        // A file's terminating "\n" ends its last line; it is not a fifth, empty line. The card
        // badge for "Wrote Created.txt" read "+5" for a four-line file (#22, DoD: statistics for
        // newly created files).
        var lines = DiffBuilder.Build(string.Empty, "alpha\nbeta\ngamma\ndelta\n");

        Assert.All(lines, line => Assert.Equal(DiffLineKind.Added, line.Kind));
        Assert.Equal(new[] { "alpha", "beta", "gamma", "delta" }, lines.Select(line => line.Text));
    }

    [Fact]
    public void Build_BothSidesEndingInNewline_DoesNotDiffThePhantomLastSegment()
    {
        var lines = DiffBuilder.Build("one\n", "one\ntwo\n");

        Assert.Equal(new[] { DiffLineKind.Context, DiffLineKind.Added }, lines.Select(line => line.Kind));
        Assert.Equal(new[] { "one", "two" }, lines.Select(line => line.Text));
    }

    [Fact]
    public void Build_SideThatIsOnlyANewline_IsOneEmptyLine()
    {
        // "\n" is one (empty) terminated line, not nothing: a created file holding a lone newline
        // must still show a line, and "\n" -> "\n\n" is one kept line plus one added.
        var created = DiffBuilder.Build(string.Empty, "\n");
        var appended = DiffBuilder.Build("\n", "\n\n");

        var only = Assert.Single(created);
        Assert.Equal((DiffLineKind.Added, string.Empty), (only.Kind, only.Text));
        Assert.Equal(new[] { DiffLineKind.Context, DiffLineKind.Added }, appended.Select(line => line.Kind));
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

        Assert.Equal(100_000, lines.Count); // the last "\n" terminates line 100_000; it is not a 100_001st
        Assert.All(lines, line =>
        {
            Assert.Equal(emptyOld ? DiffLineKind.Added : DiffLineKind.Removed, line.Kind);
            Assert.Equal(string.Empty, line.Text);
        });
        Assert.True(emptyAllocation <= fallbackAllocation + 16_384,
            $"Empty side allocated {emptyAllocation} bytes versus {fallbackAllocation} for full-remove/add.");
    }
}
