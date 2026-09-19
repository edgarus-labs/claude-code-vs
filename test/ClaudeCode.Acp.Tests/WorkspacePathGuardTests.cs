using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var mklink = Process.Start(startInfo)!;
        mklink.WaitForExit(10_000);
        Assert.Equal(0, mklink.ExitCode);
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

            CreateJunction(junctionPath, outside);

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

    [Theory]
    [InlineData("")]
    [InlineData("secret.txt")]
    public void TryResolveWithinWorkspace_WindowsDanglingJunction_IsRejected(string leaf)
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
            CreateJunction(junctionPath, outside);
            // A junction outlives its target: the reparse point now names a location outside the
            // workspace that does not exist, so opening through it reports ERROR_FILE_NOT_FOUND /
            // ERROR_PATH_NOT_FOUND exactly like a component that was never created.
            Directory.Delete(outside);

            string candidate = leaf.Length == 0 ? junctionPath : Path.Combine(junctionPath, leaf);

            Assert.False(WorkspacePathGuard.TryResolveWithinWorkspace(workspace, candidate, out _));
        }
        finally
        {
            Directory.Delete(junctionPath);
            Directory.Delete(workspace, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
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
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(security);

            using (var lease = WorkspacePathGuard.AcquireFile(workspace, file.FullName))
                lease.WriteAllText("private replacement");

            Assert.Equal("private replacement", File.ReadAllText(file.FullName));
            // Creation may change SE_DACL_AUTO_INHERITED bookkeeping without changing access.
            FileSecurity replacementSecurity = file.GetAccessControl();
            Assert.True(replacementSecurity.AreAccessRulesProtected);
            var rule = Assert.IsType<FileSystemAccessRule>(Assert.Single(
                replacementSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))));
            Assert.Equal(user, rule.IdentityReference);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.False(rule.IsInherited);
            Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void AcquireFile_ReadAllText_IsRepeatable()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-read-").FullName;
        string file = Path.Combine(workspace, "notes.txt");
        File.WriteAllText(file, "retained content");
        try
        {
            using var lease = WorkspacePathGuard.AcquireFile(workspace, file);

            Assert.Equal("retained content", lease.ReadAllText());
            // The lease's whole purpose is to hold the handle for the duration of the operation:
            // a read must not close it, or the sharing pin dies mid-operation.
            Assert.Equal("retained content", lease.ReadAllText());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void AcquireFile_WriteThenRead_ThroughOneLease_RoundTrips()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-write-").FullName;
        string file = Path.Combine(workspace, "notes.txt");
        File.WriteAllText(file, "original");
        try
        {
            using var lease = WorkspacePathGuard.AcquireFile(workspace, file);

            lease.WriteAllText("replacement");

            Assert.Equal("replacement", lease.ReadAllText());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void AcquireFile_ReadThenWrite_ThroughOneLease_PreservesByteOrderMark()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-bom-").FullName;
        string file = Path.Combine(workspace, "notes.txt");
        File.WriteAllText(file, "original", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            using (var lease = WorkspacePathGuard.AcquireFile(workspace, file))
            {
                Assert.Equal("original", lease.ReadAllText());

                lease.WriteAllText("replacement");
            }

            // BOM detection reads through the lease handle, so a preceding read must leave it usable.
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(file)[..3]);
            Assert.Equal("replacement", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void AcquireDocument_WindowsPermitsHostWriterWhilePinningAgainstDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-doc-").FullName;
        string file = Path.Combine(workspace, "doc.txt");
        File.WriteAllText(file, "original");
        try
        {
            using (WorkspacePathGuard.AcquireDocument(workspace, file))
            {
                // VS opens and saves the protected document by path; denying write sharing would
                // fail every editor save with a sharing violation.
                using (var editor = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(editor))
                {
                    writer.Write("saved by the host editor");
                }

                Assert.Throws<IOException>(() => File.Delete(file));
            }

            Assert.Equal("saved by the host editor", File.ReadAllText(file));
            File.Delete(file);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
