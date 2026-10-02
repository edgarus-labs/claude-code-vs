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
        var lines = DiffBuilder.Build("one", "one\n");

        Assert.Equal(DiffLineKind.Context, lines[0].Kind);
        var trailing = Assert.Single(lines.Skip(1));
        Assert.Equal(DiffLineKind.Added, trailing.Kind);
        Assert.Equal(string.Empty, trailing.Text);
    }

    [Fact]
    public void Build_TrailingNewlineRemoved_ShowsExactlyOneRemovedEmptyLine()
    {
        var lines = DiffBuilder.Build("one\ntwo\n", "one\ntwo");

        Assert.Equal(new[] { DiffLineKind.Context, DiffLineKind.Context, DiffLineKind.Removed }, lines.Select(line => line.Kind));
        Assert.Equal(new[] { "one", "two", string.Empty }, lines.Select(line => line.Text));
    }

    [Fact]
    public void Build_CreatedFileEndingInNewline_CountsOnlyItsRealLines()
    {
        var lines = DiffBuilder.Build(string.Empty, "alpha\nbeta\ngamma\ndelta\n");

        Assert.All(lines, line => Assert.Equal(DiffLineKind.Added, line.Kind));
        Assert.Equal(new[] { "alpha", "beta", "gamma", "delta" }, lines.Select(line => line.Text));
    }

    [Fact]
    public void Build_CreatedFileEndingInCrlf_CountsOnlyItsRealLines()
    {
        var lines = DiffBuilder.Build(string.Empty, "alpha\r\nbeta\r\n");

        Assert.All(lines, line => Assert.Equal(DiffLineKind.Added, line.Kind));
        Assert.Equal(new[] { "alpha", "beta" }, lines.Select(line => line.Text));
    }

    [Fact]
    public void Build_CreatedEmptyFile_ProducesNoLines() => Assert.Empty(DiffBuilder.Build(string.Empty, string.Empty));

    [Fact]
    public void Build_BothSidesEndingInNewline_DoesNotDiffThePhantomLastSegment()
    {
        var lines = DiffBuilder.Build("one\n", "one\ntwo\n");

        Assert.Equal(new[] { DiffLineKind.Context, DiffLineKind.Added }, lines.Select(line => line.Kind));
        Assert.Equal(new[] { "one", "two" }, lines.Select(line => line.Text));
    }

    [Fact]
    public void Build_BothSidesCrlfTerminated_MatchTheirLfShapeWithNoCarriageReturnLeftOver()
    {
        var lines = DiffBuilder.Build("alpha\r\nbeta\r\n", "alpha\r\ngamma\r\n");

        Assert.Equal(new[] { DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added }, lines.Select(line => line.Kind));
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, lines.Select(line => line.Text));
    }

    [Fact]
    public void Build_SideThatIsOnlyANewline_IsOneEmptyLine()
    {
        var created = DiffBuilder.Build(string.Empty, "\n");
        var appended = DiffBuilder.Build("\n", "\n\n");

        var only = Assert.Single(created);
        Assert.Equal((DiffLineKind.Added, string.Empty), (only.Kind, only.Text));
        Assert.Equal(new[] { DiffLineKind.Context, DiffLineKind.Added }, appended.Select(line => line.Kind));
    }

    [Fact]
    public void Build_AboveAlignmentCellLimit_FallsBackToFullRemoveAdd()
    {
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
        var text = new string('\n', 100_000);
        var fallbackSide = new string('\n', 21);
        _ = DiffBuilder.Build(emptyOld ? fallbackSide : text, emptyOld ? text : fallbackSide);
        _ = DiffBuilder.Build(emptyOld ? string.Empty : text, emptyOld ? text : string.Empty);

        var beforeFallback = GC.GetAllocatedBytesForCurrentThread();
        _ = DiffBuilder.Build(emptyOld ? fallbackSide : text, emptyOld ? text : fallbackSide);
        var fallbackAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeFallback;

        var beforeEmpty = GC.GetAllocatedBytesForCurrentThread();
        var lines = DiffBuilder.Build(emptyOld ? string.Empty : text, emptyOld ? text : string.Empty);
        var emptyAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeEmpty;

        Assert.Equal(100_000, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Equal(emptyOld ? DiffLineKind.Added : DiffLineKind.Removed, line.Kind);
            Assert.Equal(string.Empty, line.Text);
        });
        Assert.True(emptyAllocation <= fallbackAllocation + 16_384,
            $"Empty side allocated {emptyAllocation} bytes versus {fallbackAllocation} for full-remove/add.");
    }
}
