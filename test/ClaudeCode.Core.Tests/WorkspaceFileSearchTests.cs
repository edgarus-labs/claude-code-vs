using ClaudeCode.Core.ViewModels;
using System;
using System.IO;
using System.Threading;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class WorkspaceFileSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-search-" + Guid.NewGuid().ToString("N"));

    public WorkspaceFileSearchTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateFile(params string[] segments)
    {
        var path = Path.Combine(_root, Path.Combine(segments));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private void CreateFiller(int count, params string[] folder)
    {
        for (var i = 0; i < count; i++) CreateFile([.. folder, "filler" + i + ".js"]);
    }

    private static string Suffix(string reference) => Path.DirectorySeparatorChar + reference;

    // A web project's node_modules can hold more entries than the whole walk may visit. It must not
    // spend them before the source folders are searched, or a source file reads as missing.
    [Fact]
    public void FindBySuffix_BuildOrToolFolderLargerThanTheBudget_StillFindsTheSourceFile()
    {
        var target = CreateFile("src", "OrderService.cs");
        CreateFiller(50, "web", "node_modules", "pkg");

        var found = WorkspaceFileSearch.FindBySuffix(_root, Suffix("OrderService.cs"), maxEntries: 20, CancellationToken.None);

        Assert.Equal(target, Assert.Single(found), ignoreCase: true);
    }

    // A walk cut short cannot tell one match from the first of several: presenting the one it saw
    // as the file would open an arbitrary namesake, so the click must hear the search was partial.
    [Fact]
    public void FindBySuffix_BudgetRunsOutAfterOneMatch_ReportsTheSearchIncompleteInsteadOfAUniqueFile()
    {
        CreateFile("z", "Foo.cs");
        CreateFiller(50, "m");
        CreateFile("a", "Foo.cs");

        var ex = Assert.Throws<IOException>(() => WorkspaceFileSearch.FindBySuffix(_root, Suffix("Foo.cs"), maxEntries: 20, CancellationToken.None));

        Assert.Contains("too many", ex.Message, StringComparison.Ordinal);
    }

    // Closing the chat must stop a walk over a large tree instead of letting it run to the cap.
    [Fact]
    public void FindBySuffix_Cancelled_StopsTheWalk()
    {
        CreateFile("src", "Foo.cs");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => WorkspaceFileSearch.FindBySuffix(_root, Suffix("Foo.cs"), cancelled.Token));
    }
}
