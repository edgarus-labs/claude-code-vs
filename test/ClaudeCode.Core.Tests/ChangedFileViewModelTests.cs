using ClaudeCode.Core.ViewModels;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChangedFileViewModelTests
{
    private static ChangedFileViewModel Create(string? originalText) =>
        new ChangedFileViewModel(@"C:\ws\file.txt", originalText, _ => Task.CompletedTask, _ => Task.CompletedTask);

    [Fact]
    public void UpdateCounts_NewEmptyFile_ReportsNoLineChanges()
    {
        var vm = Create(originalText: null);

        vm.UpdateCounts(string.Empty);

        Assert.Equal(0, vm.AddedLines);
        Assert.Equal(0, vm.RemovedLines);
    }

    [Fact]
    public void UpdateCounts_SingleLineFileTruncatedToEmpty_ReportsOnlyTheRemoval()
    {
        var vm = Create("hello");

        vm.UpdateCounts(string.Empty);

        Assert.Equal(0, vm.AddedLines);
        Assert.Equal(1, vm.RemovedLines);
    }

    [Fact]
    public void UpdateCounts_NewFileWithTrailingNewline_ReportsExactLineCountWithoutInflation()
    {
        var vm = Create(originalText: null);

        vm.UpdateCounts("hello\n");

        Assert.Equal(1, vm.AddedLines);
        Assert.Equal(0, vm.RemovedLines);
    }

    [Fact]
    public void UpdateCounts_NewFileWithWindowsTrailingNewline_ReportsExactLineCountWithoutInflation()
    {
        var vm = Create(originalText: null);

        vm.UpdateCounts("hello\r\nworld\r\n");

        Assert.Equal(2, vm.AddedLines);
        Assert.Equal(0, vm.RemovedLines);
    }

    [Fact]
    public void UpdateCounts_SingleLineWithTrailingNewlineTruncatedToEmpty_ReportsOnlyTheRemoval()
    {
        var vm = Create("hello\n");

        vm.UpdateCounts(string.Empty);

        Assert.Equal(0, vm.AddedLines);
        Assert.Equal(1, vm.RemovedLines);
    }

    [Fact]
    public void UpdateCounts_NullCurrentText_TreatedAsEmptyWithoutThrowing()
    {
        var vm = Create("hello\n");

        vm.UpdateCounts(null!);

        Assert.Equal(0, vm.AddedLines);
        Assert.Equal(1, vm.RemovedLines);
    }
}
