using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed partial class ChatSessionStateTests
{
    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; }

        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "cc-vs-workspace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string PathUnder(string relative) => Path.Combine(Root, relative);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<(ChatViewModel Vm, RecordingAcpAgentConnection Connection, StubChatSessionServices Services)> ConnectWithWorkspaceAsync(string workspaceRoot)
    {
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), workspaceRoot);
        var vm = new ChatViewModel(services);
        await vm.Initialization;
        return (vm, connection, services);
    }

    // C1 (CRITICAL, arbitrary-file-read-write): the single most important test in this wave. A
    // write request whose path resolves outside the workspace must be rejected before any disk
    // I/O and must never create the target file.
    [Fact]
    public async Task FileWriteRequest_PathOutsideWorkspace_IsRejectedWithoutTouchingDisk()
    {
        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var escapedPath = outside.PathUnder("evil.txt");
        Assert.False(File.Exists(escapedPath));

        var request = connection.RaiseFileWriteRequested(escapedPath, "malicious content");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => request.Response.Task);
        Assert.False(File.Exists(escapedPath));
    }

    [Fact]
    public async Task FileWriteRequest_SiblingDirectorySharingPrefix_IsRejected()
    {
        using var workspace = new TempWorkspace();
        // "<root>-secret" starts with the same characters as "<root>" but is not a child of it: a
        // naive StartsWith(root) containment check would wrongly let this through.
        var siblingRoot = workspace.Root + "-secret";
        Directory.CreateDirectory(siblingRoot);
        try
        {
            var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
            using var _vm = vm;
            var escapedPath = Path.Combine(siblingRoot, "evil.txt");

            var request = connection.RaiseFileWriteRequested(escapedPath, "malicious content");

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => request.Response.Task);
            Assert.False(File.Exists(escapedPath));
        }
        finally
        {
            Directory.Delete(siblingRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FileReadRequest_PathOutsideWorkspace_IsRejected()
    {
        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        var secretPath = outside.PathUnder("secret.txt");
        File.WriteAllText(secretPath, "top secret");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        var request = connection.RaiseFileReadRequested(secretPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => request.Response.Task);
    }

    [Fact]
    public async Task FileWriteRequest_PathInsideWorkspace_Succeeds()
    {
        using var workspace = new TempWorkspace();
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var targetPath = workspace.PathUnder("notes.txt");

        var request = connection.RaiseFileWriteRequested(targetPath, "hello workspace");

        Assert.True(await request.Response.Task);
        Assert.Equal("hello workspace", File.ReadAllText(targetPath));
    }

    [Fact]
    public async Task FileReadRequest_PathInsideWorkspace_Succeeds()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("notes.txt");
        File.WriteAllText(targetPath, "hello workspace");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        var request = connection.RaiseFileReadRequested(targetPath);

        Assert.Equal("hello workspace", await request.Response.Task);
    }

    // H6 (disk-not-live-buffer): a live, unsaved editor buffer must win over the on-disk contents.
    [Fact]
    public async Task FileReadRequest_LiveBufferOpen_ReturnsBufferTextInsteadOfDisk()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("open.cs");
        File.WriteAllText(targetPath, "stale disk contents");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var fullPath = Path.GetFullPath(targetPath);
        services.OpenDocuments[fullPath] = "unsaved live buffer contents";

        var request = connection.RaiseFileReadRequested(targetPath);

        Assert.Equal("unsaved live buffer contents", await request.Response.Task);
    }

    [Fact]
    public async Task FileWriteRequest_LiveBufferOpen_UpdatesBufferInsteadOfDisk()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("open.cs");
        File.WriteAllText(targetPath, "original disk contents");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var fullPath = Path.GetFullPath(targetPath);
        services.OpenDocuments[fullPath] = "original buffer contents";

        var request = connection.RaiseFileWriteRequested(targetPath, "edited buffer contents");

        Assert.True(await request.Response.Task);
        Assert.Equal("edited buffer contents", services.OpenDocuments[fullPath]);
        Assert.Equal("original disk contents", File.ReadAllText(targetPath));
    }

    [Fact]
    public async Task FileWriteRequest_EditorRejectsEdit_DoesNotOverwriteDisk()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("read-only.cs");
        File.WriteAllText(targetPath, "original disk contents");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        services.OpenDocuments[targetPath] = "unsaved buffer contents";
        services.WriteOpenDocumentHandler = (_, _, _) =>
            Task.FromException<bool>(new IOException("The editor rejected the edit."));

        var request = connection.RaiseFileWriteRequested(targetPath, "replacement");

        await Assert.ThrowsAsync<IOException>(() => request.Response.Task);
        Assert.Equal("original disk contents", File.ReadAllText(targetPath));
        Assert.Equal("unsaved buffer contents", services.OpenDocuments[targetPath]);
    }

    [Fact]
    public async Task FileRequests_NoWorkspace_DenyAccessWithoutChangingDisk()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("existing.txt");
        File.WriteAllText(targetPath, "original");
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService());
        using var vm = new ChatViewModel(services);
        await vm.Initialization;

        var read = connection.RaiseFileReadRequested(targetPath);
        var write = connection.RaiseFileWriteRequested(targetPath, "replacement");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => read.Response.Task);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => write.Response.Task);
        Assert.Equal("original", File.ReadAllText(targetPath));
    }

    [Fact]
    public async Task FileReadRequest_ParentReplacedDuringEditorAwait_DoesNotReadOutside()
    {
        if (OperatingSystem.IsWindows()) return;

        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        var parent = workspace.PathUnder("folder");
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "file.txt"), "workspace text");
        File.WriteAllText(outside.PathUnder("file.txt"), "outside secret");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        services.ReadOpenDocumentHandler = (_, _) =>
        {
            Directory.Move(parent, workspace.PathUnder("original-folder"));
            Directory.CreateSymbolicLink(parent, outside.Root);
            return Task.FromResult<string?>(null);
        };

        var request = connection.RaiseFileReadRequested(Path.Combine(parent, "file.txt"));

        Assert.Equal("workspace text", await request.Response.Task);
    }

    [Fact]
    public async Task FileWriteRequest_ParentReplacedDuringEditorAwait_DoesNotWriteOutside()
    {
        if (OperatingSystem.IsWindows()) return;

        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        var parent = workspace.PathUnder("folder");
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "file.txt"), "workspace text");
        File.WriteAllText(outside.PathUnder("file.txt"), "outside secret");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        services.WriteOpenDocumentHandler = (_, _, _) =>
        {
            Directory.Move(parent, workspace.PathUnder("original-folder"));
            Directory.CreateSymbolicLink(parent, outside.Root);
            return Task.FromResult(false);
        };

        var request = connection.RaiseFileWriteRequested(Path.Combine(parent, "file.txt"), "replacement");

        Assert.True(await request.Response.Task);
        Assert.Equal("outside secret", File.ReadAllText(outside.PathUnder("file.txt")));
        Assert.Equal("replacement", File.ReadAllText(workspace.PathUnder("original-folder/file.txt")));
    }

    [Fact]
    public async Task FileReadRequest_LeafReplacedDuringEditorAwait_ReadsAcquiredFile()
    {
        if (OperatingSystem.IsWindows()) return;

        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        var target = workspace.PathUnder("file.txt");
        var secret = outside.PathUnder("secret.txt");
        File.WriteAllText(target, "workspace text");
        File.WriteAllText(secret, "outside secret");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        services.ReadOpenDocumentHandler = (_, _) =>
        {
            File.Delete(target);
            File.CreateSymbolicLink(target, secret);
            return Task.FromResult<string?>(null);
        };

        var request = connection.RaiseFileReadRequested(target);

        Assert.Equal("workspace text", await request.Response.Task);
    }

    [Fact]
    public async Task FileWriteRequest_LeafReplacedDuringEditorAwait_ReplacesLinkNotItsTarget()
    {
        if (OperatingSystem.IsWindows()) return;

        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        var target = workspace.PathUnder("file.txt");
        var secret = outside.PathUnder("secret.txt");
        File.WriteAllText(target, "workspace text", new UTF8Encoding(true));
        File.WriteAllText(secret, "outside secret");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        services.WriteOpenDocumentHandler = (_, _, _) =>
        {
            File.Delete(target);
            File.CreateSymbolicLink(target, secret);
            return Task.FromResult(false);
        };

        var request = connection.RaiseFileWriteRequested(target, "replacement");

        Assert.True(await request.Response.Task);
        Assert.Equal("outside secret", File.ReadAllText(secret));
        Assert.Equal("replacement", File.ReadAllText(target));
        Assert.Null(new FileInfo(target).LinkTarget);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(target)[..3]);
    }

    // M10 (encoding-not-preserved): a round trip through the file broker must not silently drop a
    // byte-order mark, and must not leave a temp file behind after an atomic write.
    [Fact]
    public async Task FileWriteRequest_PreservesUtf8BomAndCleansUpTempFile()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("bom.txt");
        File.WriteAllText(targetPath, "original", new UTF8Encoding(true));
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        var request = connection.RaiseFileWriteRequested(targetPath, "updated content");
        Assert.True(await request.Response.Task);

        var bytes = File.ReadAllBytes(targetPath);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("updated content", File.ReadAllText(targetPath));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp*"));
    }

    [Fact]
    public async Task FileWriteRequest_NoExistingFile_WritesUtf8WithoutBom()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("new.txt");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        var request = connection.RaiseFileWriteRequested(targetPath, "brand new content");
        Assert.True(await request.Response.Task);

        var bytes = File.ReadAllBytes(targetPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    // M11 (crlf-normalized-on-partial-read): a partial (line/limit) read of a CRLF file must keep
    // the carriage returns, not silently normalize them to bare LF — and must not hand back a
    // dangling "\r" that is not followed by the "\n" the document actually has there.
    [Fact]
    public async Task FileReadRequest_PartialRangeOfCrlfFile_PreservesCarriageReturns()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("crlf.txt");
        File.WriteAllText(targetPath, "line1\r\nline2\r\nline3", new UTF8Encoding(false));
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        var request = connection.RaiseFileReadRequested(targetPath, line: 1, limit: 2);

        var text = await request.Response.Task;
        Assert.Equal("line1\r\nline2", text);
    }

    [Fact]
    public async Task FileReadRequest_SingleLineOfCrlfFile_HasNoDanglingCarriageReturn()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("crlf.txt");
        File.WriteAllText(targetPath, "line1\r\nline2\r\nline3", new UTF8Encoding(false));
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        var request = connection.RaiseFileReadRequested(targetPath, line: 2, limit: 1);

        var text = await request.Response.Task;
        Assert.Equal("line2", text);
    }

    // A partial read re-emits each line's own terminator: picking one terminator for the whole
    // slice from a whole-file scan rewrites the interior separators of a mixed-ending document, and
    // the agent then uses that text as the old_text of its follow-up Edit.
    [Fact]
    public async Task FileReadRequest_PartialRangeOfMixedEndingFile_KeepsEachLinesOwnTerminator()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("mixed.txt");
        File.WriteAllText(targetPath, "a\nb\r\nc\n", new UTF8Encoding(false));
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        var text = await connection.RaiseFileReadRequested(targetPath, line: 1, limit: 2).Response.Task;

        Assert.Equal("a\nb", text);
    }

    // The requested window is clamped to the file: a limit past EOF returns what is there, a line
    // past the last line returns nothing, and an empty file has no lines at all.
    [Fact]
    public async Task FileReadRequest_PartialRangeOutsideTheFile_ClampsInsteadOfOverreading()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("crlf.txt");
        File.WriteAllText(targetPath, "line1\r\nline2\r\nline3", new UTF8Encoding(false));
        var emptyPath = workspace.PathUnder("empty.txt");
        File.WriteAllText(emptyPath, string.Empty, new UTF8Encoding(false));
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        Assert.Equal("line3", await connection.RaiseFileReadRequested(targetPath, line: 3, limit: 10).Response.Task);
        Assert.Equal(string.Empty, await connection.RaiseFileReadRequested(targetPath, line: 9, limit: 1).Response.Task);
        Assert.Equal(string.Empty, await connection.RaiseFileReadRequested(emptyPath, line: 1, limit: 5).Response.Task);
    }
}
