using System;
using System.IO;
using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class WorkspacePathGuardTests
{
    private static readonly string _root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private static string Combine(params string[] segments)
    {
        string path = _root;
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
        }
        return path;
    }

    [Fact]
    public void TryResolveWithinWorkspace_PathInsideRoot_Succeeds()
    {
        var candidate = Combine("src", "Foo.cs");

        var result = WorkspacePathGuard.TryResolveWithinWorkspace(_root, candidate, out var fullPath);

        Assert.True(result);
        Assert.Equal(Path.GetFullPath(candidate), fullPath);
    }

    [Fact]
    public void TryResolveWithinWorkspace_RootItself_Succeeds()
    {
        var result = WorkspacePathGuard.TryResolveWithinWorkspace(_root, _root, out var fullPath);

        Assert.True(result);
        Assert.Equal(Path.GetFullPath(_root), fullPath);
    }

    [Fact]
    public void TryResolveWithinWorkspace_SiblingDirectoryWithSamePrefix_IsRejected()
    {
        // "C:\repo-secret" must not pass a naive StartsWith("C:\repo") check.
        var sibling = _root + "-secret" + Path.DirectorySeparatorChar + "file.txt";

        var result = WorkspacePathGuard.TryResolveWithinWorkspace(_root, sibling, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryResolveWithinWorkspace_ParentTraversalEscapingRoot_IsRejected()
    {
        var traversal = Combine("..", "..", "outside.txt");

        var result = WorkspacePathGuard.TryResolveWithinWorkspace(_root, traversal, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryResolveWithinWorkspace_UncPath_IsRejected()
    {
        var result = WorkspacePathGuard.TryResolveWithinWorkspace(_root, @"\\attacker\share\x.txt", out _);

        Assert.False(result);
    }

    [Fact]
    public void TryResolveWithinWorkspace_DeviceNamespacePath_IsRejected()
    {
        var result = WorkspacePathGuard.TryResolveWithinWorkspace(_root, @"\\?\C:\repo\file.txt", out _);

        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryResolveWithinWorkspace_NoWorkspaceRoot_IsRejected(string? root)
    {
        var result = WorkspacePathGuard.TryResolveWithinWorkspace(root, Combine("file.txt"), out _);

        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryResolveWithinWorkspace_NoCandidatePath_IsRejected(string? candidate)
    {
        var result = WorkspacePathGuard.TryResolveWithinWorkspace(_root, candidate, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryResolveWithinWorkspace_CaseInsensitiveOnWindowsStyleRoot_Succeeds()
    {
        var upperCased = Combine("SRC", "Foo.cs").ToUpperInvariant();

        var result = WorkspacePathGuard.TryResolveWithinWorkspace(_root, upperCased, out var fullPath);

        Assert.True(result);
        Assert.Equal(Path.GetFullPath(upperCased), fullPath);
    }
}
