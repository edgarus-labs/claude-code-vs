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

    private static void RaiseToolCallOn(RecordingAcpAgentConnection connection, string id, params string[] locations) =>
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate
        {
            ToolCallId = id,
            Title = "Read",
            Status = ToolCallStatus.Completed,
            Locations = locations,
        }));

    // Issue #39. The agent names a file it has just read by its bare name - "`Program.cs:12`" - so
    // resolving that against the workspace root reports an existing file as missing. The absolute
    // path the Read tool call carried is the precise reference the name stands for.
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

    // Directory components the agent did write narrow the match rather than being dropped.
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

    // Two files of that name were read: picking either would open an arbitrary one.
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

    // The status a click on a reference nothing resolves reports: the baseline that tells a more
    // specific refusal apart from "not found" without pinning either message's wording.
    // Overwrites vm.StatusMessage, so read the status under test first.
    private static async Task<string?> NotFoundStatusAsync(ChatViewModel vm, string reference)
    {
        // A missing file in the workspace root itself: a missing folder fails earlier, differently.
        var missing = "missing-" + reference;
        await vm.OpenFileReferenceAsync(Href(missing));
        return vm.StatusMessage?.Replace(missing, reference, StringComparison.Ordinal);
    }

    // The same file reported by several calls - read, then edited, under two spellings - is one
    // file, not two namesakes; otherwise every file Claude edited would read as ambiguous.
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

    // The reference matches whole path segments: "Program.cs" is not the tail of "MyProgram.cs".
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

    // A reference whose path already exists under the workspace root is authoritative: a namesake
    // the agent happened to read elsewhere must not win over the file the path names.
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

    // The locations are agent-supplied: one outside the workspace must be refused like any other
    // path - and reported as that, not as a file that does not exist.
    [Fact]
    public async Task OpenFileReference_BareNameOfAFileTheAgentReadOutsideTheWorkspace_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return; // see OpenFileReference_FileThatDoesNotExist_ReportsWithoutFaulting
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

    // Issue #39 as reproduced in VS: Claude found the files with Grep and never read them. Glob
    // and Grep report what they found relative to the session cwd - the workspace root - so that
    // relative path is the precise reference a bare "`OrderService.cs:5`" in the answer stands for,
    // even with a namesake elsewhere in the workspace.
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

    // A relative search result is anchored on the workspace root, never trusted to stay in it: one
    // that climbs out is refused like any other agent-supplied path.
    [Fact]
    public async Task OpenFileReference_BareNameOfASearchResultThatClimbsOutOfTheWorkspace_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return; // see OpenFileReference_FileThatDoesNotExist_ReportsWithoutFaulting
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

    // A namesake outside the workspace could never be opened, so it must not make the one inside
    // it ambiguous - e.g. a README.md in the git root above a solution in a subfolder.
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

    // Locations are agent-supplied strings. One the runtime cannot parse - a null character, or on
    // .NET Framework (the VS host) any of "<>| - must neither break the session update that carried
    // it (it runs on the UI thread) nor stop the valid location beside it from resolving. The bad
    // character sits in a folder so each location really ends in a "Program.cs" segment and reaches the
    // resolution step. This TFM (net10.0) does not reproduce the .NET Framework throw from
    // Path.IsPathRooted for "<>|; IsRootedLocation's pre-check for it is not exercised here.
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

    // A location the runtime cannot parse names no file at all, so it is no namesake - like a
    // location with no file behind it - and must not turn the click into a refusal that skips the
    // workspace search for the file that does exist.
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

    // What the agent read belongs to the conversation that read it: after a new session the name
    // no longer picks the file the previous one read out of its namesakes.
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

    // Claude often guesses where a file is before it finds it: the failed Read of the guess still
    // reported its location. A path with no file behind it is not a namesake of the one that exists.
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

    // A failed session/load puts the user back in the conversation they were in, so its file
    // references must keep resolving against what the agent read in it - picking that file out of
    // its namesakes, as before the load.
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

    // The case left over once Claude's own paths are exhausted: it names a file it only saw in a
    // shell command's output ("git show --stat" listing "AcpProcessConnectionTests.cs") - no tool
    // call reported it. The file exists in the workspace, so the link has to open it.
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

    // Directories the reference does name narrow the workspace search the same way they narrow the
    // tool-call locations: whole segments, so "Views/Chat.cs" is not "ViewModels/Chat.cs".
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

    // Two files of the name in the workspace and nothing to tell them apart: opening either would
    // be a guess, so the click says so and opens nothing.
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

    // A build copies content files into bin/obj ("appsettings.json" under bin\Debug\net8.0): that
    // copy is not a second file the user means, so the source one opens.
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

    // Every file that exists is openable, a generated one that lives only in obj included.
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

    // The workspace search is the last resort: a file Claude did read wins over its namesakes.
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
