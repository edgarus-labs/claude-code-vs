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
    // the carriage returns, not silently normalize them to bare LF.
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
        Assert.Equal("line1\r\nline2\r", text);
    }
}
