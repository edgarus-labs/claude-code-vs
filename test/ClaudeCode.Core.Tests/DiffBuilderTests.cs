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
    public void Build_TrailingNewlineRemoved_ShowsExactlyOneRemovedEmptyLine()
    {
        // The delete mirror of the case above. The sides disagree about the terminator, so neither
        // may be stripped: were the test "either side ends in \n" rather than "both do", both sides
        // would strip to the same two lines and deleting a file's final newline would render as no
        // change at all.
        var lines = DiffBuilder.Build("one\ntwo\n", "one\ntwo");

        Assert.Equal(new[] { DiffLineKind.Context, DiffLineKind.Context, DiffLineKind.Removed }, lines.Select(line => line.Kind));
        Assert.Equal(new[] { "one", "two", string.Empty }, lines.Select(line => line.Text));
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
    public void Build_CreatedFileEndingInCrlf_CountsOnlyItsRealLines()
    {
        // CRLF is the norm in a Visual Studio workspace, so this is the #22 badge case on Windows.
        // The terminator is recognised on the raw text while the split normalises "\r\n" to "\n"
        // beforehand; the two agree only because a CRLF-terminated text also ends in '\n'. Nothing
        // else pins that agreement, so changing either helper alone would quietly bring back "+3"
        // for a two-line file - or leave a stray "\r" glued to the end of every line.
        var lines = DiffBuilder.Build(string.Empty, "alpha\r\nbeta\r\n");

        Assert.All(lines, line => Assert.Equal(DiffLineKind.Added, line.Kind));
        Assert.Equal(new[] { "alpha", "beta" }, lines.Select(line => line.Text));
    }

    [Fact]
    public void Build_CreatedEmptyFile_ProducesNoLines()
    {
        // An empty file is zero lines, not one empty one: without the split's empty-text guard,
        // "".Split('\n') yields a single empty segment and an empty file would render one phantom
        // line - the same off-by-one that made the #22 badge overcount.
        Assert.Empty(DiffBuilder.Build(string.Empty, string.Empty));
    }

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
        // The terminator is recognised on the raw text while the split normalises "\r\n" first; if
        // those two ever disagree a CRLF file grows a phantom empty last line. An un-normalised
        // split would instead glue "\r" onto every line, so lines that are in fact identical stop
        // comparing equal and the whole file diffs as removed-then-added.
        var lines = DiffBuilder.Build("alpha\r\nbeta\r\n", "alpha\r\ngamma\r\n");

        Assert.Equal(new[] { DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added }, lines.Select(line => line.Kind));
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, lines.Select(line => line.Text));
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
        // Both sides end in "\n", so each split drops its terminator segment: the fallback side is
        // 21 lines and the big side 100_000. 21 * 100_000 = 2_100_000 exceeds _maxAlignmentCells
        // (2_000_000), so the nonempty comparison takes the bounded full-remove/add path and its
        // allocation is a meaningful baseline. 20 would strip to exactly 2_000_000 cells - not
        // above the limit - and the baseline would silently become an 8 MB LCS table instead.
        var text = new string('\n', 100_000);
        var fallbackSide = new string('\n', 21);
        // Warm up on the very shapes being measured, not on toy strings: the first large Build on a
        // thread costs an extra ~1 MB of allocation-context accounting, which lands in whichever
        // measurement window runs first and swallows the ~400 KB table this guard looks for.
        _ = DiffBuilder.Build(emptyOld ? fallbackSide : text, emptyOld ? text : fallbackSide);
        _ = DiffBuilder.Build(emptyOld ? string.Empty : text, emptyOld ? text : string.Empty);

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
