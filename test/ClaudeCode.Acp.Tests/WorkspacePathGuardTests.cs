using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
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
    public void TryResolveWithinWorkspace_PathCase_MatchesPlatformSemantics()
    {
        var upperCased = Combine("SRC", "Foo.cs").ToUpperInvariant();

        var result = WorkspacePathGuard.TryResolveWithinWorkspace(_root, upperCased, out var fullPath);

        Assert.Equal(OperatingSystem.IsWindows(), result);
        Assert.Equal(OperatingSystem.IsWindows() ? Path.GetFullPath(upperCased) : string.Empty, fullPath);
    }

    [Fact]
    public void TryResolveWithinWorkspace_PathThroughJunctionEscapingRoot_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            // NTFS junctions are a Windows-only reparse-point mechanism; nothing to verify elsewhere.
            return;
        }

        string workspace = Directory.CreateTempSubdirectory("wpg-workspace-").FullName;
        string outside = Directory.CreateTempSubdirectory("wpg-outside-").FullName;
        string junctionPath = Path.Combine(workspace, "link");

        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "top secret");

            var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{outside}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var mklink = Process.Start(startInfo)!;
            mklink.WaitForExit(10_000);
            Assert.Equal(0, mklink.ExitCode);

            var candidate = Path.Combine(junctionPath, "secret.txt");

            var result = WorkspacePathGuard.TryResolveWithinWorkspace(workspace, candidate, out _);

            // The junction lexically resolves under `workspace`, but the real target lives in
            // `outside`: containment must be evaluated against the reparse-resolved path.
            Assert.False(result);
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }

            Directory.Delete(workspace, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Theory]
    [InlineData("secret.txt")]
    [InlineData("new.txt")]
    public void TryResolveWithinWorkspace_UnixSymlinkOutsideRoot_IsRejected(string leaf)
    {
        if (OperatingSystem.IsWindows()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-workspace-").FullName;
        string outside = Directory.CreateTempSubdirectory("wpg-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), outside);

            Assert.False(WorkspacePathGuard.TryResolveWithinWorkspace(workspace, Path.Combine(workspace, "link", leaf), out _));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void TryResolveWithinWorkspace_UnixDanglingSymlink_IsRejected()
    {
        if (OperatingSystem.IsWindows()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-workspace-").FullName;
        try
        {
            File.CreateSymbolicLink(Path.Combine(workspace, "dangling"), Path.Combine(workspace, "..", "missing-" + Guid.NewGuid().ToString("N")));

            Assert.False(WorkspacePathGuard.TryResolveWithinWorkspace(workspace, Path.Combine(workspace, "dangling"), out _));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void AcquireDocument_WindowsBlocksPathReplacementUntilDisposed()
    {
        if (!OperatingSystem.IsWindows()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-lease-").FullName;
        string folder = Directory.CreateDirectory(Path.Combine(workspace, "folder")).FullName;
        string file = Path.Combine(folder, "file.txt");
        File.WriteAllText(file, "original");
        try
        {
            using (WorkspacePathGuard.AcquireDocument(workspace, file))
            {
                Assert.Throws<IOException>(() => Directory.Move(folder, Path.Combine(workspace, "moved")));
                Assert.Throws<IOException>(() => File.Move(file, Path.Combine(folder, "moved.txt")));
                Assert.Equal("original", File.ReadAllText(file));
            }

            File.Move(file, Path.Combine(folder, "moved.txt"));
            Directory.Move(folder, Path.Combine(workspace, "moved"));
            Assert.Equal("original", File.ReadAllText(Path.Combine(workspace, "moved", "moved.txt")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void AcquireFile_WindowsAtomicWrite_PreservesRestrictedDacl()
    {
        if (!OperatingSystem.IsWindows()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-acl-").FullName;
        var file = new FileInfo(Path.Combine(workspace, "private.txt"));
        File.WriteAllText(file.FullName, "private original");
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(security);
            string originalDacl = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            using (var lease = WorkspacePathGuard.AcquireFile(workspace, file.FullName))
                lease.WriteAllText("private replacement");

            Assert.Equal("private replacement", File.ReadAllText(file.FullName));
            Assert.Equal(originalDacl, file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.True(file.GetAccessControl().AreAccessRulesProtected);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
