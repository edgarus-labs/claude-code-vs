using ClaudeCode.Core.ViewModels;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed partial class ChatSessionStateTests
{
    private static string Href(string path, int? line = null) =>
        ChatFileReference.LinkPrefix + "path=" + Uri.EscapeDataString(path) +
        (line.HasValue ? "&line=" + line.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);

    // The agent writes workspace-relative paths. WorkspacePathGuard resolves a relative candidate
    // against the *process* working directory (devenv's, not the solution's), so the reference has
    // to be anchored on the workspace root before it is validated - otherwise every relative
    // reference either fails to open or, worse, opens something outside the workspace.
    [Fact]
    public async Task OpenFileReference_RelativePathInANestedFolder_OpensTheWorkspaceFile()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("src"));
        var target = workspace.PathUnder(Path.Combine("src", "Program.cs"));
        File.WriteAllText(target, "class P { }");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href("src/Program.cs"));

        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
        Assert.Null(Assert.Single(services.OpenedDocumentLines));
    }

    [Fact]
    public async Task OpenFileReference_WithLine_HandsTheLineToTheHostEditor()
    {
        using var workspace = new TempWorkspace();
        var target = workspace.PathUnder("Program.cs");
        File.WriteAllText(target, "a\nb\nc\n");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href("Program.cs", 42));

        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
        Assert.Equal(42, Assert.Single(services.OpenedDocumentLines));
    }

    [Fact]
    public async Task OpenFileReference_AbsolutePathInsideTheWorkspace_OpensIt()
    {
        using var workspace = new TempWorkspace();
        var target = workspace.PathUnder("Absolute.cs");
        File.WriteAllText(target, "x");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href(target));

        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    // The reference text is agent-authored and untrusted: a traversal must never reach the host's
    // document opener, which has no workspace notion of its own.
    [Fact]
    public async Task OpenFileReference_EscapingTheWorkspace_IsRefusedAndReported()
    {
        using var workspace = new TempWorkspace();
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href("../secrets.txt"));

        Assert.Empty(services.OpenedDocumentPaths);
        Assert.Contains("secrets.txt", vm.StatusMessage!, StringComparison.Ordinal);
    }

    // The rooted branch of the candidate anchoring hands the reference to the guard untouched, so
    // it - not the relative branch above - is what stands between agent markdown naming
    // C:\Users\me\.ssh\id_rsa and the host opening it.
    [Fact]
    public async Task OpenFileReference_AbsolutePathOutsideTheWorkspace_IsRefusedAndReported()
    {
        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        var secret = outside.PathUnder("secrets.txt");
        File.WriteAllText(secret, "top secret");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href(secret));

        Assert.Empty(services.OpenedDocumentPaths);
        Assert.Contains("secrets.txt", vm.StatusMessage!, StringComparison.Ordinal);
    }

    // In the VSIX, WorkspaceRoot is a live callback into solution state that throws while a
    // solution is closing or reloading. The only caller discards this task on the WebView2
    // callback thread, so the "never faults" contract has to cover that read too.
    [Fact]
    public async Task OpenFileReference_WhenTheHostCannotReportTheWorkspaceRoot_ReportsWithoutFaulting()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(workspace.PathUnder("Program.cs"), "x");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        services.WorkspaceRootHandler = () => throw new InvalidOperationException("The solution is closing.");

        await vm.OpenFileReferenceAsync(Href("Program.cs"));

        Assert.Empty(services.OpenedDocumentPaths);
        Assert.Contains("Program.cs", vm.StatusMessage!, StringComparison.Ordinal);
    }

    // AC: an invalid reference must not break the chat. Nothing may fault out of the call, and the
    // reason has to reach the user instead of disappearing.
    [Fact]
    public async Task OpenFileReference_FileThatDoesNotExist_ReportsWithoutFaulting()
    {
        // The existence pin is a Windows document-lease guarantee; elsewhere the lease has nothing
        // to protect and the host is the only thing that can report a missing file.
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href("gone.cs"));

        Assert.Empty(services.OpenedDocumentPaths);
        Assert.Contains("gone.cs", vm.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenFileReference_WhenTheHostCannotOpenTheFile_ReportsWithoutFaulting()
    {
        using var workspace = new TempWorkspace();
        var target = workspace.PathUnder("locked.cs");
        File.WriteAllText(target, "x");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        services.OpenDocumentHandler = (_, _, _) => Task.FromException(new InvalidOperationException("Editor refused"));

        await vm.OpenFileReferenceAsync(Href("locked.cs"));

        Assert.Contains("locked.cs", vm.StatusMessage!, StringComparison.Ordinal);
    }

    // The href crosses the WebView2 boundary, so it is as untrusted as the markdown it came from.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/")]
    [InlineData("/__claudecode/open?path=")]
    public async Task OpenFileReference_HrefThisRendererNeverEmitted_OpensNothing(string? href)
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(workspace.PathUnder("Program.cs"), "x");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(href);

        Assert.Empty(services.OpenedDocumentPaths);
    }
}
