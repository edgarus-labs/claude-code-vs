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

    [Theory]
    [InlineData(@"\\attacker\share\x.txt")]
    [InlineData(@"\\?\C:\repo\file.txt")]
    [InlineData(@"\\.\C:\repo\file.txt")]
    public void TryResolveWithinWorkspace_UncOrDeviceNamespacePath_IsRejected(string candidate)
    {
        // docs/VsControlProtocol.md states that UNC and device-namespace paths are rejected. Under
        // a local root such as C:\repo, lexical containment already refuses every spelling of
        // them (Path.GetFullPath keeps the \\ root, which can never sit under a drive letter), so
        // no row here can tell the guard's leading-separator gate apart from containment. The
        // gate stays as root-independent defence in depth for that documented contract; it is
        // only observable under a non-local root, which needs a reachable share to exercise.
        Assert.False(WorkspacePathGuard.TryResolveWithinWorkspace(_root, candidate, out _));
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
            if (Directory.Exists(junctionPath)) Directory.Delete(junctionPath);
            Directory.Delete(workspace, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>
    /// Creates a directory chain under <paramref name="root"/> deep enough that a leaf named
    /// <paramref name="leaf"/> inside it is at least MAX_PATH (260) characters long, and returns
    /// the deepest directory. .NET's own file APIs prefix <c>\\?\</c> internally, so the chain can
    /// be created on a long-path-disabled host as well as a long-path-enabled one.
    /// </summary>
    private static string CreateDirectoryChainBeyondMaxPath(string root, string leaf)
    {
        string deep = root;
        while (Path.Combine(deep, leaf).Length < 260)
        {
            deep = Path.Combine(deep, new string('p', 40));
        }

        Directory.CreateDirectory(deep);
        return deep;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryResolveWithinWorkspace_PathBeyondMaxPathInsideRoot_Resolves(bool leafAlreadyExists)
    {
        if (!OperatingSystem.IsWindows())
        {
            // MAX_PATH normalization is a Win32 concept; Linux reports ENAMETOOLONG, never ENOENT.
            return;
        }

        string workspace = Directory.CreateTempSubdirectory("wpg-deep-").FullName;
        try
        {
            string candidate = Path.Combine(CreateDirectoryChainBeyondMaxPath(workspace, "deep.txt"), "deep.txt");
            Assert.True(candidate.Length >= 260);
            if (leafAlreadyExists)
            {
                File.WriteAllText(candidate, "deep");
            }

            // Containment is a property of the path alone. It must not depend on the host's
            // LongPathsEnabled registry value, and it must not depend on whether the leaf exists
            // yet - otherwise creating a file and reading it back answer differently at the same
            // length. Both configurations agree because the guard addresses the object through the
            // \\?\ form, where a missing component is reported as missing at any length.
            Assert.True(WorkspacePathGuard.TryResolveWithinWorkspace(workspace, candidate, out var fullPath));
            Assert.Equal(Path.GetFullPath(candidate), fullPath);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void TryResolveWithinWorkspace_JunctionEscapingRootBeyondMaxPath_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            // NTFS junctions are a Windows-only reparse-point mechanism; nothing to verify elsewhere.
            return;
        }

        string workspace = Directory.CreateTempSubdirectory("wpg-long-").FullName;
        string outside = Directory.CreateTempSubdirectory("wpg-outside-").FullName;
        string junctionPath = Path.Combine(workspace, "link");
        string deepJunctionPath = junctionPath;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "top secret");
            CreateJunction(junctionPath, outside);

            // cmd.exe and mklink are Win32 callers and cannot create a junction past MAX_PATH, so
            // create it short and relocate the reparse point itself into a >MAX_PATH location.
            deepJunctionPath = Path.Combine(CreateDirectoryChainBeyondMaxPath(workspace, "link"), "link");
            Directory.Move(junctionPath, deepJunctionPath);

            var candidate = Path.Combine(deepJunctionPath, "secret.txt");

            // The guard opens the candidate through the \\?\ form, so the open succeeds at this
            // length on either host configuration and GetFinalPathNameByHandleW names the real
            // target under `outside`. Containment is therefore decided by the reparse-resolved
            // path rather than by whether MAX_PATH normalization happened to reject the string.
            Assert.False(WorkspacePathGuard.TryResolveWithinWorkspace(workspace, candidate, out _));
        }
        finally
        {
            if (Directory.Exists(deepJunctionPath)) Directory.Delete(deepJunctionPath);
            if (Directory.Exists(junctionPath)) Directory.Delete(junctionPath);
            Directory.Delete(workspace, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void TryResolveWithinWorkspace_DanglingJunctionBeyondMaxPath_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            // NTFS junctions are a Windows-only reparse-point mechanism; nothing to verify elsewhere.
            return;
        }

        string workspace = Directory.CreateTempSubdirectory("wpg-long-dangling-").FullName;
        string outside = Directory.CreateTempSubdirectory("wpg-outside-").FullName;
        string junctionPath = Path.Combine(workspace, "link");
        string deepJunctionPath = junctionPath;
        try
        {
            CreateJunction(junctionPath, outside);
            deepJunctionPath = Path.Combine(CreateDirectoryChainBeyondMaxPath(workspace, "link"), "link");
            Directory.Move(junctionPath, deepJunctionPath);
            Directory.Delete(outside);

            // This is the case the length refusal was added for. Opening through a junction whose
            // target is gone reports ERROR_PATH_NOT_FOUND exactly like a component that was never
            // created, so the only thing stopping the ancestor walk from stripping the live
            // reparse point and re-attaching its name to a canonicalized ancestor inside the
            // workspace is GetFileAttributesW seeing the link's own attributes - which, at this
            // length, it can only do through the \\?\ form. Unprovable absence must fail closed.
            Assert.False(WorkspacePathGuard.TryResolveWithinWorkspace(workspace, Path.Combine(deepJunctionPath, "secret.txt"), out _));
            Assert.False(WorkspacePathGuard.TryResolveWithinWorkspace(workspace, deepJunctionPath, out _));
        }
        finally
        {
            if (Directory.Exists(deepJunctionPath)) Directory.Delete(deepJunctionPath);
            if (Directory.Exists(junctionPath)) Directory.Delete(junctionPath);
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

    [Fact]
    public void AcquireFile_CommittedWrite_IsNotReportedAsAFailure_WhenTheNewLeafCannotBeReopened()
    {
        if (!OperatingSystem.IsWindows()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-reopen-").FullName;
        string path = Path.Combine(workspace, "notes.txt");
        File.WriteAllText(path, "original");
        try
        {
            using (var lease = WorkspacePathGuard.AcquireFile(workspace, path))
            {
                // Withhold only FILE_READ_DATA from the owner: the atomic replacement still
                // commits (the temporary inherits this DACL and the rename needs delete, not
                // read), but reopening the new leaf with GENERIC_READ afterwards fails with
                // ERROR_ACCESS_DENIED - the deterministic stand-in for the sharing violation an
                // indexer or virus scanner causes on the freshly renamed destination.
                SetOwnerOnlyRights(path, FileSystemRights.FullControl & ~FileSystemRights.ReadData);

                lease.WriteAllText("replacement");
            }

            SetOwnerOnlyRights(path, FileSystemRights.FullControl);
            Assert.Equal("replacement", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void AcquireDocument_KeepsPinningTheLeafAgainstDeletion_AfterAWrite()
    {
        if (!OperatingSystem.IsWindows()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-doc-write-").FullName;
        string file = Path.Combine(workspace, "doc.txt");
        File.WriteAllText(file, "original");
        try
        {
            using (var lease = WorkspacePathGuard.AcquireDocument(workspace, file))
            {
                lease.WriteAllText("replacement");

                // A document lease withholds FILE_SHARE_DELETE for its whole life. Replacing the
                // entry is not a reason to surrender the pin the lease exists to hold.
                Assert.Throws<IOException>(() => File.Delete(file));
            }

            Assert.Equal("replacement", File.ReadAllText(file));
            File.Delete(file);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void AcquireDocument_FailedWrite_KeepsTheLeafPinnedAndReadable()
    {
        if (!OperatingSystem.IsWindows()) return;

        string workspace = Directory.CreateTempSubdirectory("wpg-doc-fail-").FullName;
        string file = Path.Combine(workspace, "doc.txt");
        File.WriteAllText(file, "original");
        try
        {
            using (var lease = WorkspacePathGuard.AcquireDocument(workspace, file))
            {
                // A reader without delete sharing (an indexer or scanner holding the destination)
                // makes the rename-replace fail with a sharing violation after the temporary was
                // written, so the write fails at the exact step the lease has surrendered its pin.
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    Assert.Throws<IOException>(() => lease.WriteAllText("replacement"));
                }

                // The replacement never committed, so the lease must be exactly as it was before
                // the write: the original entry readable through it and still pinned against
                // deletion for as long as the lease lives.
                Assert.Equal("original", lease.ReadAllText());
                Assert.Throws<IOException>(() => File.Delete(file));
            }

            Assert.Equal("original", File.ReadAllText(file));
            Assert.Empty(Directory.GetFiles(workspace, ".claude-*.tmp"));
            File.Delete(file);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcquireLease_PathBeyondMaxPathInsideRoot_RoundTrips(bool document)
    {
        if (!OperatingSystem.IsWindows())
        {
            // MAX_PATH normalization is a Win32 concept; Linux reports ENAMETOOLONG, never ENOENT.
            return;
        }

        string workspace = Directory.CreateTempSubdirectory("wpg-deep-lease-").FullName;
        try
        {
            string file = Path.Combine(CreateDirectoryChainBeyondMaxPath(workspace, "deep.txt"), "deep.txt");
            Assert.True(file.Length >= 260);
            File.WriteAllText(file, "original");

            // The guard resolves this path on every host, so the lease has to pin and replace it
            // on every host too: its raw Win32 calls must address the object through the \\?\
            // form exactly like the guard does, or the resolution the guard just granted is
            // unusable wherever LongPathsEnabled is off or the process is not longPathAware.
            using (var lease = document ? WorkspacePathGuard.AcquireDocument(workspace, file) : WorkspacePathGuard.AcquireFile(workspace, file))
            {
                Assert.Equal("original", lease.ReadAllText());

                lease.WriteAllText("replacement");

                Assert.Equal("replacement", lease.ReadAllText());
                if (document) Assert.Throws<IOException>(() => File.Delete(file));
            }

            Assert.Equal("replacement", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetOwnerOnlyRights(string path, FileSystemRights rights)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
