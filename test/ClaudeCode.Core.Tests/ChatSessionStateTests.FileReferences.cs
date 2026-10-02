using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed partial class ChatSessionStateTests
{
    private static string Href(string path, int? line = null) =>
        ChatFileReference.LinkPrefix + "path=" + Uri.EscapeDataString(path) +
        (line.HasValue ? "&line=" + line.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);

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

    [Fact]
    public async Task OpenFileReference_FileThatDoesNotExist_ReportsWithoutFaulting()
    {
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

    private static void RaiseToolCallOn(RecordingAcpAgentConnection connection, string id, params string[] locations) =>
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate
        {
            ToolCallId = id,
            Title = "Read",
            Status = ToolCallStatus.Completed,
            Locations = locations,
        }));

    [Fact]
    public async Task OpenFileReference_BareNameOfAFileTheAgentRead_OpensThatFile()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder(Path.Combine("src", "App")));
        var target = workspace.PathUnder(Path.Combine("src", "App", "Program.cs"));
        File.WriteAllText(target, "class P { }");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", target);

        await vm.OpenFileReferenceAsync(Href("Program.cs", 12));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
        Assert.Equal(12, Assert.Single(services.OpenedDocumentLines));
    }

    [Fact]
    public async Task OpenFileReference_PartialPathOfAFileTheAgentRead_OpensThatFile()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder(Path.Combine("src", "ViewModels")));
        Directory.CreateDirectory(workspace.PathUnder(Path.Combine("src", "Views")));
        var target = workspace.PathUnder(Path.Combine("src", "ViewModels", "Chat.cs"));
        var namesake = workspace.PathUnder(Path.Combine("src", "Views", "Chat.cs"));
        File.WriteAllText(target, "x");
        File.WriteAllText(namesake, "y");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", namesake, target);

        await vm.OpenFileReferenceAsync(Href("ViewModels/Chat.cs"));

        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_BareNameMatchingTwoFilesTheAgentRead_ReportsAmbiguityAndOpensNothing()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("a"));
        Directory.CreateDirectory(workspace.PathUnder("b"));
        var first = workspace.PathUnder(Path.Combine("a", "Foo.cs"));
        var second = workspace.PathUnder(Path.Combine("b", "Foo.cs"));
        File.WriteAllText(first, "x");
        File.WriteAllText(second, "y");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", first);
        RaiseToolCallOn(connection, "read-2", second);

        await vm.OpenFileReferenceAsync(Href("Foo.cs"));

        Assert.Empty(services.OpenedDocumentPaths);
        var status = vm.StatusMessage;
        Assert.Contains("Foo.cs", status!, StringComparison.Ordinal);
        Assert.NotEqual(await NotFoundStatusAsync(vm, "Foo.cs"), status);
    }

    private static async Task<string?> NotFoundStatusAsync(ChatViewModel vm, string reference)
    {
        var missing = "missing-" + reference;
        await vm.OpenFileReferenceAsync(Href(missing));
        return vm.StatusMessage?.Replace(missing, reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenFileReference_BareNameOfAFileTheAgentReadThenEdited_OpensThatFile()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("src"));
        var target = workspace.PathUnder(Path.Combine("src", "Program.cs"));
        File.WriteAllText(target, "x");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", target);
        RaiseToolCallOn(connection, "edit-1", workspace.PathUnder(Path.Combine("src", "..", "src", "Program.cs")));

        await vm.OpenFileReferenceAsync(Href("Program.cs"));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Theory]
    [InlineData("Program.cs", "MyProgram.cs")]
    [InlineData("Models/Chat.cs", "ViewModels/Chat.cs")]
    public async Task OpenFileReference_ReferenceThatIsOnlyATextualSuffixOfAnotherFile_OpensTheFileItNames(string reference, string lookalike)
    {
        using var workspace = new TempWorkspace();
        var target = workspace.PathUnder(Path.Combine("a", reference));
        var decoy = workspace.PathUnder(Path.Combine("b", lookalike));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        File.WriteAllText(target, "x");
        File.WriteAllText(decoy, "y");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", decoy, target);

        await vm.OpenFileReferenceAsync(Href(reference));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Theory]
    [InlineData("Foo.cs")]
    [InlineData("b/Foo.cs")]
    public async Task OpenFileReference_PathThatExistsUnderTheWorkspace_OpensItRatherThanANamesakeTheAgentRead(string reference)
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("a"));
        Directory.CreateDirectory(workspace.PathUnder(Path.Combine("x", "b")));
        Directory.CreateDirectory(workspace.PathUnder("b"));
        var namesake = workspace.PathUnder(Path.Combine("a", "Foo.cs"));
        var nestedNamesake = workspace.PathUnder(Path.Combine("x", "b", "Foo.cs"));
        File.WriteAllText(namesake, "x");
        File.WriteAllText(nestedNamesake, "xb");
        File.WriteAllText(workspace.PathUnder("Foo.cs"), "root");
        File.WriteAllText(workspace.PathUnder(Path.Combine("b", "Foo.cs")), "b");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", namesake, nestedNamesake);

        await vm.OpenFileReferenceAsync(Href(reference));

        Assert.Equal(Path.GetFullPath(workspace.PathUnder(reference)), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_BareNameOfAFileTheAgentReadOutsideTheWorkspace_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        var secret = outside.PathUnder("secrets.cs");
        File.WriteAllText(secret, "top secret");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", secret);

        await vm.OpenFileReferenceAsync(Href("secrets.cs"));

        Assert.Empty(services.OpenedDocumentPaths);
        var status = vm.StatusMessage;
        Assert.Contains("secrets.cs", status!, StringComparison.Ordinal);
        Assert.NotEqual(await NotFoundStatusAsync(vm, "secrets.cs"), status);
    }

    [Fact]
    public async Task OpenFileReference_BareNameOfAFileTheAgentFoundBySearch_OpensThatFile()
    {
        using var workspace = new TempWorkspace();
        var relative = Path.Combine("src", "Shop.Core", "Services", "OrderService.cs");
        var namesake = workspace.PathUnder(Path.Combine("legacy", "OrderService.cs"));
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.PathUnder(relative))!);
        Directory.CreateDirectory(Path.GetDirectoryName(namesake)!);
        File.WriteAllText(workspace.PathUnder(relative), "x");
        File.WriteAllText(namesake, "y");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "grep-1", relative);

        await vm.OpenFileReferenceAsync(Href("OrderService.cs", 5));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(workspace.PathUnder(relative)), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
        Assert.Equal(5, Assert.Single(services.OpenedDocumentLines));
    }

    [Fact]
    public async Task OpenFileReference_BareNameOfASearchResultThatClimbsOutOfTheWorkspace_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        var secret = outside.PathUnder("secrets.cs");
        File.WriteAllText(secret, "top secret");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "grep-1", Path.GetRelativePath(workspace.Root, secret));

        await vm.OpenFileReferenceAsync(Href("secrets.cs"));

        Assert.Empty(services.OpenedDocumentPaths);
        var status = vm.StatusMessage;
        Assert.Contains("secrets.cs", status!, StringComparison.Ordinal);
        Assert.NotEqual(await NotFoundStatusAsync(vm, "secrets.cs"), status);
    }

    [Fact]
    public async Task OpenFileReference_BareNameMatchingOneFileInsideAndOneOutsideTheWorkspace_OpensTheOneInside()
    {
        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("docs"));
        var target = workspace.PathUnder(Path.Combine("docs", "README.md"));
        var outsider = outside.PathUnder("README.md");
        File.WriteAllText(target, "inside");
        File.WriteAllText(outsider, "outside");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", outsider, target);

        await vm.OpenFileReferenceAsync(Href("README.md"));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_ToolCallWithUnparseableLocations_StillShowsTheCallAndResolvesTheValidOne()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("src"));
        var target = workspace.PathUnder(Path.Combine("src", "Program.cs"));
        File.WriteAllText(target, "x");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        RaiseToolCallOn(connection, "read-1", workspace.PathUnder(Path.Combine("a\0b", "Program.cs")), workspace.PathUnder(Path.Combine("a|b", "Program.cs")), target);
        await vm.OpenFileReferenceAsync(Href("Program.cs"));

        Assert.Single(vm.Messages.SelectMany(message => message.ToolCalls), tool => tool.ToolCallId == "read-1");
        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_OnlyAnUnparseableLocationMatches_OpensTheWorkspaceFileOfThatName()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("src"));
        var target = workspace.PathUnder(Path.Combine("src", "Program.cs"));
        File.WriteAllText(target, "x");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        RaiseToolCallOn(connection, "read-1", workspace.PathUnder(Path.Combine("a\0b", "Program.cs")));
        await vm.OpenFileReferenceAsync(Href("Program.cs"));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_AfterANewSession_NoLongerResolvesAgainstThePreviousSessionsReads()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("a"));
        Directory.CreateDirectory(workspace.PathUnder("b"));
        var read = workspace.PathUnder(Path.Combine("a", "Foo.cs"));
        File.WriteAllText(read, "x");
        File.WriteAllText(workspace.PathUnder(Path.Combine("b", "Foo.cs")), "y");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", read);

        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.OpenFileReferenceAsync(Href("Foo.cs"));

        Assert.Empty(services.OpenedDocumentPaths);
        Assert.Contains("Foo.cs", vm.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenFileReference_BareNameOfAFileTheAgentFoundAfterAFailedGuess_OpensTheFileThatExists()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("src"));
        var target = workspace.PathUnder(Path.Combine("src", "Program.cs"));
        File.WriteAllText(target, "x");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", workspace.PathUnder(Path.Combine("lib", "Program.cs")));
        RaiseToolCallOn(connection, "read-2", target);

        await vm.OpenFileReferenceAsync(Href("Program.cs"));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_AfterAFailedSessionLoad_StillResolvesAgainstTheRestoredConversationsReads()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("src"));
        Directory.CreateDirectory(workspace.PathUnder("legacy"));
        var target = workspace.PathUnder(Path.Combine("src", "Program.cs"));
        File.WriteAllText(target, "x");
        File.WriteAllText(workspace.PathUnder(Path.Combine("legacy", "Program.cs")), "y");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", target);
        connection.LoadSessionHandler = (_, _, _, _) =>
            Task.FromException<NewSessionResult>(new InvalidOperationException("Agent restarted"));
        await vm.OpenSessionAsync(new SessionSummary("session-2", workspace.Root, "Older chat", null));
        Assert.Contains("Could not open session", vm.StatusMessage!, StringComparison.Ordinal);

        await vm.OpenFileReferenceAsync(Href("Program.cs"));

        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_BareNameNoToolReported_OpensTheOneFileOfThatNameInTheWorkspace()
    {
        using var workspace = new TempWorkspace();
        var target = workspace.PathUnder(Path.Combine("test", "ClaudeCode.Acp.Tests", "AcpProcessConnectionTests.cs"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "x");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href("AcpProcessConnectionTests.cs", 1214));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
        Assert.Equal(1214, Assert.Single(services.OpenedDocumentLines));
    }

    [Fact]
    public async Task OpenFileReference_PartialPathNoToolReported_OpensTheFileThoseFoldersName()
    {
        using var workspace = new TempWorkspace();
        var target = workspace.PathUnder(Path.Combine("src", "Views", "Chat.cs"));
        var other = workspace.PathUnder(Path.Combine("src", "ViewModels", "Chat.cs"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(target, "x");
        File.WriteAllText(other, "y");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href("Views/Chat.cs"));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_BareNameNoToolReportedMatchingTwoWorkspaceFiles_ReportsAmbiguityAndOpensNothing()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("a"));
        Directory.CreateDirectory(workspace.PathUnder("b"));
        File.WriteAllText(workspace.PathUnder(Path.Combine("a", "Foo.cs")), "x");
        File.WriteAllText(workspace.PathUnder(Path.Combine("b", "Foo.cs")), "y");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href("Foo.cs"));

        Assert.Empty(services.OpenedDocumentPaths);
        var status = vm.StatusMessage;
        Assert.Contains("Foo.cs", status!, StringComparison.Ordinal);
        Assert.NotEqual(await NotFoundStatusAsync(vm, "Foo.cs"), status);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    public async Task OpenFileReference_BareNameNoToolReportedWithACopyInBuildOutput_OpensTheSourceFile(string buildFolder)
    {
        using var workspace = new TempWorkspace();
        var target = workspace.PathUnder(Path.Combine("src", "App", "appsettings.json"));
        var copy = workspace.PathUnder(Path.Combine("src", "App", buildFolder, "Debug", "net8.0", "appsettings.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(target, "{}");
        File.WriteAllText(copy, "{}");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href("appsettings.json"));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_BareNameNoToolReportedOnlyInBuildOutput_OpensIt()
    {
        using var workspace = new TempWorkspace();
        var target = workspace.PathUnder(Path.Combine("src", "App", "obj", "Debug", "App.g.cs"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "x");
        var (vm, _, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        await vm.OpenFileReferenceAsync(Href("App.g.cs"));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(target), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    [Fact]
    public async Task OpenFileReference_BareNameOfAFileTheAgentReadWithANamesakeInTheWorkspace_OpensTheOneItRead()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathUnder("a"));
        Directory.CreateDirectory(workspace.PathUnder("b"));
        var read = workspace.PathUnder(Path.Combine("b", "Foo.cs"));
        File.WriteAllText(workspace.PathUnder(Path.Combine("a", "Foo.cs")), "x");
        File.WriteAllText(read, "y");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        RaiseToolCallOn(connection, "read-1", read);

        await vm.OpenFileReferenceAsync(Href("Foo.cs"));

        Assert.Null(vm.StatusMessage);
        Assert.Equal(Path.GetFullPath(read), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

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
