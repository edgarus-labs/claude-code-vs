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
}
