using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed partial class ChatSessionStateTests
{
    [Fact]
    public async Task AgentWrite_TracksChangedFileOnce_AndRejectRestoresOriginalContent()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Program.cs");
        Directory.CreateDirectory(workspace.PathUnder("sub"));
        File.WriteAllText(targetPath, "line a\nline b\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        Assert.True(await connection.RaiseFileWriteRequested(targetPath, "line a\nline c\nline d\n").Response.Task);
        // A second spelling of the same file: de-duplication keys on the canonicalized path, so two
        // spellings must collapse to one row rather than tracking (and offering to revert) twice.
        var secondSpelling = Path.Combine(workspace.Root, "sub", "..", "program.cs");
        Assert.True(await connection.RaiseFileWriteRequested(secondSpelling, "line a\nline c\nline d\nline e\n").Response.Task);

        var file = Assert.Single(vm.ChangedFiles);
        Assert.Equal("Program.cs", file.Name);
        Assert.False(file.IsNew);
        Assert.Equal(3, file.AddedLines);
        Assert.Equal(1, file.RemovedLines);

        await file.RejectCommand.ExecuteAsync(null);

        Assert.Equal("line a\nline b\n", File.ReadAllText(targetPath));
        Assert.Empty(vm.ChangedFiles);
    }

    [Fact]
    public async Task AgentCreatesFile_RejectDeletesIt_AcceptKeepsIt()
    {
        using var workspace = new TempWorkspace();
        var created = workspace.PathUnder("New.cs");
        var kept = workspace.PathUnder("Kept.cs");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        Assert.True(await connection.RaiseFileWriteRequested(created, "brand new").Response.Task);
        Assert.True(await connection.RaiseFileWriteRequested(kept, "also new").Response.Task);
        Assert.Equal(2, vm.ChangedFiles.Count);
        Assert.All(vm.ChangedFiles, file => Assert.True(file.IsNew));

        await vm.ChangedFiles.Single(file => file.Name == "New.cs").RejectCommand.ExecuteAsync(null);
        await vm.ChangedFiles.Single(file => file.Name == "Kept.cs").AcceptCommand.ExecuteAsync(null);

        Assert.False(File.Exists(created));
        Assert.Equal("also new", File.ReadAllText(kept));
        Assert.Empty(vm.ChangedFiles);
    }

    [Fact]
    public async Task RejectAllChanges_RestoresEveryFile_AndNewSessionClearsTheList()
    {
        using var workspace = new TempWorkspace();
        var first = workspace.PathUnder("a.txt");
        var second = workspace.PathUnder("b.txt");
        File.WriteAllText(first, "A0");
        File.WriteAllText(second, "B0");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        Assert.True(await connection.RaiseFileWriteRequested(first, "A1").Response.Task);
        Assert.True(await connection.RaiseFileWriteRequested(second, "B1").Response.Task);
        await vm.RejectAllChangesCommand.ExecuteAsync(null);
        Assert.Equal("A0", File.ReadAllText(first));
        Assert.Equal("B0", File.ReadAllText(second));
        Assert.Empty(vm.ChangedFiles);

        Assert.True(await connection.RaiseFileWriteRequested(first, "A2").Response.Task);
        Assert.Single(vm.ChangedFiles);
        connection.NewSessionHandler = _ => Task.FromResult(new ClaudeCode.Contracts.NewSessionResult("session-2", []));
        await vm.NewSessionCommand.ExecuteAsync(null);
        Assert.Empty(vm.ChangedFiles);
        Assert.Equal("A2", File.ReadAllText(first)); // a new session forgets the list; it never reverts silently.
    }

    [Fact]
    public async Task ToolCallDiff_TracksFileWrittenByTheAgentProcess_AndRejectRestoresIt()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Edited.cs");
        File.WriteAllText(targetPath, "before\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task; // keep the turn in flight while tool calls arrive
        vm.InputText = "edit it";
        var sending = vm.SendAsync();

        var pending = new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Edited.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "before\n", NewText = "after\nmore\n" }],
        };
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(pending));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);

        File.WriteAllText(targetPath, "after\nmore\n"); // the agent process writes the file itself
        var completed = new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Edited.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
            Content = pending.Content,
        };
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(completed));
        await WaitUntilAsync(() => vm.ChangedFiles[0].AddedLines == 2);

        var file = Assert.Single(vm.ChangedFiles);
        Assert.Equal("Edited.cs", file.Name);
        Assert.Equal(1, file.RemovedLines);

        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        await file.RejectCommand.ExecuteAsync(null);
        Assert.Equal("before\n", File.ReadAllText(targetPath));
        Assert.Empty(vm.ChangedFiles);
    }

    [Fact]
    public async Task ReplayedToolCalls_FromASessionResume_AreNotTrackedAsChanges()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Old.cs");
        File.WriteAllText(targetPath, "already applied\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        connection.LoadSessionHandler = (sessionId, cwd, mcpServers, _) =>
        {
            connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
            {
                ToolCallId = "old-1", Title = "Edit Old.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
                Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "x", NewText = "already applied\n" }],
            }), sessionId);
            // A replayed message chunk is the positive control: it proves the replay was processed,
            // so the emptiness assertion below is not just a race the test happened to win.
            connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.AgentMessageChunk("replayed reply"), sessionId);
            return Task.FromResult(new ClaudeCode.Contracts.NewSessionResult(sessionId, []));
        };

        await vm.OpenSessionCommand.ExecuteAsync(new ClaudeCode.Contracts.SessionSummary("old", workspace.Root, "Old chat", null));
        await WaitUntilAsync(() => vm.Messages.Count > 0);

        Assert.Empty(vm.ChangedFiles);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out waiting for the changed-file state");
    }

    [Fact]
    public async Task OpenChangedFileCommand_AsksHostToOpenThePath()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("open-me.cs");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        Assert.True(await connection.RaiseFileWriteRequested(targetPath, "x").Response.Task);

        await vm.OpenChangedFileCommand.ExecuteAsync(vm.ChangedFiles[0]);

        Assert.Equal(Path.GetFullPath(targetPath), Assert.Single(services.OpenedDocumentPaths), ignoreCase: true);
    }

    // The agent process writes its own Edit/Write results and does not synchronize with the
    // notification that carries them, so the first diff-bearing update can arrive after the file
    // already changed. The one shape whose diff carries the whole file is claude-agent-acp's Write
    // update when the structured patch is empty (src/diff.ts: oldText = originalFile, newText =
    // content); its oldText is then the authoritative pre-write content, and snapshotting the
    // file at that moment would make Reject write the edit back over itself and report success.
    [Fact]
    public async Task ToolCallDiff_WriteUpdateFirstSeenAfterTheWriteLanded_RestoresTheReportedOriginalFile()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Edited.cs");
        File.WriteAllText(targetPath, "before\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "rewrite it";
        var sending = vm.SendAsync();

        File.WriteAllText(targetPath, "after\nmore\n"); // the write lands before the notification
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Edited.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "before\n", NewText = "after\nmore\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.False(file.IsNew);
        Assert.True(file.CanRevert);
        await file.RejectCommand.ExecuteAsync(null);

        Assert.Equal("before\n", File.ReadAllText(targetPath));
        Assert.Empty(vm.ChangedFiles);
    }

    // claude-agent-acp never sends whole-file text for an Edit: the pending tool_call carries the
    // model's old_string/new_string (src/tools.ts, case "Edit") and the completed update carries
    // per-hunk context and +/- lines (src/diff.ts), so a snapshot taken after the edit landed can
    // never be recognised by content equality. It is recognised by what it lacks - the text the edit
    // replaced - and such a row must not offer a revert that would only write the edit back over
    // itself and report success.
    [Fact]
    public async Task ToolCallDiff_EditSnippetsFirstSeenAfterTheEditLanded_CannotOfferARevert()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Edited.cs");
        File.WriteAllText(targetPath, "before\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "edit it";
        var sending = vm.SendAsync();

        File.WriteAllText(targetPath, "after\nmore\n"); // the edit lands before the notification
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Edited.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "before", NewText = "after\nmore" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.False(file.IsNew);
        Assert.False(file.CanRevert);
        Assert.False(file.RejectCommand.CanExecute(null));
        await file.RejectCommand.ExecuteAsync(null);
        await vm.RejectAllChangesCommand.ExecuteAsync(null);

        Assert.Equal("after\nmore\n", File.ReadAllText(targetPath));
        Assert.Single(vm.ChangedFiles); // the agent did change it; the row stays, without a revert
        Assert.Contains("1 of 1", vm.StatusMessage!, StringComparison.Ordinal);
    }

    // An edit that keeps its old text (appending after it) leaves the snapshot looking pre-edit,
    // so the race is only visible once the call completes: the file the tool reports as changed
    // still equals the snapshot, which means the snapshot was the post-edit content.
    [Fact]
    public async Task ToolCallDiff_PendingSnapshotTakenAfterAnAppendingEditLanded_CannotOfferARevert()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Edited.cs");
        File.WriteAllText(targetPath, "before\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "append to it";
        var sending = vm.SendAsync();

        File.WriteAllText(targetPath, "before\nmore\n"); // accept-edits mode: the CLI edits as soon as the message completes
        var pending = new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Edited.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "before", NewText = "before\nmore" }],
        };
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(pending));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Edited.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
            // src/diff.ts joins the hunk's context and +/- lines: [" before", "+more"] → old "before", new "before\nmore".
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "before", NewText = "before\nmore" }],
        }));
        await WaitUntilAsync(() => !vm.ChangedFiles[0].CanRevert);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        await file.RejectCommand.ExecuteAsync(null);

        Assert.Equal("before\nmore\n", File.ReadAllText(targetPath));
        Assert.Single(vm.ChangedFiles);
    }

    // The diff on a pending tool_call is the model's own input (src/tools.ts, case "Edit"), and
    // claude-agent-acp emits it before permission is asked. Its oldText is evidence of nothing
    // until the call actually ran: a denied Edit whose newText echoes the file must not leave a
    // row whose Reject writes the model's oldText over a file the agent was never allowed to touch.
    [Fact]
    public async Task ToolCallDiff_OnAPendingCallWhoseNewTextEchoesTheFile_NeverAdoptsItsOldTextAsTheRestoreContent()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Guarded.cs");
        File.WriteAllText(targetPath, "the user's work\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "touch it";
        var sending = vm.SendAsync();

        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Guarded.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "x", NewText = "the user's work\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        await vm.RejectAllChangesCommand.ExecuteAsync(null);

        Assert.Equal("the user's work\n", File.ReadAllText(targetPath));
    }

    // A denied or failed call changed nothing (claude-agent-acp reports both as status "failed"),
    // so its row must go: left behind, "Reject all" would rewrite the file with its own content and
    // the panel would count a file the agent never touched. A file the agent did change earlier in
    // the session keeps its row.
    [Fact]
    public async Task ToolCallDiff_WhoseCallEndsFailed_RemovesTheRowWhenTheFileIsUnchanged()
    {
        using var workspace = new TempWorkspace();
        var edited = workspace.PathUnder("Edited.cs");
        var created = workspace.PathUnder("New.cs");
        var earlier = workspace.PathUnder("Earlier.cs");
        File.WriteAllText(edited, "before\n");
        File.WriteAllText(earlier, "before\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        Assert.True(await connection.RaiseFileWriteRequested(earlier, "changed\n").Response.Task);
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "edit them";
        var sending = vm.SendAsync();

        ClaudeCode.Contracts.ToolCallUpdate Update(string id, string path, string? oldText, string newText, ClaudeCode.Contracts.ToolCallStatus status) => new()
        {
            ToolCallId = id, Title = "Edit " + Path.GetFileName(path), Kind = "edit", Status = status,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = path, OldText = oldText, NewText = newText }],
        };
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update("edit-1", edited, "before", "after", ClaudeCode.Contracts.ToolCallStatus.Pending)));
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update("write-1", created, null, "brand new\n", ClaudeCode.Contracts.ToolCallStatus.Pending)));
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update("edit-2", earlier, "changed", "changed again", ClaudeCode.Contracts.ToolCallStatus.Pending)));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 3);

        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update("edit-1", edited, "before", "after", ClaudeCode.Contracts.ToolCallStatus.Failed)));
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update("write-1", created, null, "brand new\n", ClaudeCode.Contracts.ToolCallStatus.Failed)));
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update("edit-2", earlier, "changed", "changed again", ClaudeCode.Contracts.ToolCallStatus.Failed)));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        Assert.Equal("Earlier.cs", Assert.Single(vm.ChangedFiles).Name);
        Assert.Equal("before\n", File.ReadAllText(edited));
        Assert.False(File.Exists(created));
    }

    // The ledger insert and the panel row are two steps, the second posted to the dispatcher. A
    // New Chat landing between them (the write arrives on the JSON-RPC read loop, and a remote turn
    // needs no local prompt) used to leave the new session a row with nothing behind it.
    [Fact]
    public async Task AgentWrite_RacingANewChat_DoesNotLeaveAPhantomRowInTheNewSession()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("racy.txt");
        File.WriteAllText(targetPath, "before");
        var ui = new QueuedSynchronizationContext();
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), workspace.Root);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        ClaudeCode.Core.ViewModels.ChatViewModel vm;
        try { vm = new ClaudeCode.Core.ViewModels.ChatViewModel(services); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        while (!vm.Initialization.IsCompleted) { ui.Drain(); await Task.Yield(); }
        await vm.Initialization;

        var write = await Task.Run(() => connection.RaiseFileWriteRequested(targetPath, "after"));
        Assert.True(await write.Response.Task);
        // The row insert is still queued behind the dispatcher when New Chat runs on it.
        connection.NewSessionHandler = _ => Task.FromResult(new ClaudeCode.Contracts.NewSessionResult("session-2", []));
        await WithDispatcherInstalled(ui, () => vm.NewSessionAsync());
        ui.Drain();

        Assert.Empty(vm.ChangedFiles);
        vm.Dispose();
        ui.Drain();
    }

    // A brand-new file's *pending* notification can itself already be racing the agent's own write,
    // not only its completed one (contrast ToolCallDiff_WriteUpdateFirstSeenAfterTheWriteLanded_
    // RestoresTheReportedOriginalFile above): the read behind that pending update finds the file
    // already created and pins a non-null snapshot equal to the new content on the row. A second
    // notification for the same path returns that already-tracked row without ever re-reading the
    // file, so the completed update's oldText - the one signal that could still correct it - was
    // discarded, leaving a newly created file showing "+0 -0" instead of its real additions.
    [Fact]
    public async Task ToolCallDiff_NewFilePendingSnapshotTakenAfterTheWriteLanded_ShowsItsRealAdditions()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Created.cs");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "create it";
        var sending = vm.SendAsync();

        File.WriteAllText(targetPath, "brand new\nfile\n"); // the write lands before any notification
        var pending = new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = null, NewText = "brand new\nfile\n" }],
        };
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(pending));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);

        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
            // The structured patch was empty (src/diff.ts): oldText = originalFile, which is "" for a
            // file that did not exist before this write.
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "", NewText = "brand new\nfile\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles[0].AddedLines == 2);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.Equal(2, file.AddedLines);
        Assert.Equal(0, file.RemovedLines);
    }

    // claude-agent-acp reports an Edit/Write in three notifications (dist/acp-agent.js, dist/tools.js):
    // the tool_call with the model's optimistic diff, a PostToolUse-hook tool_call_update carrying the
    // real structuredPatch diff but no status (parsed as Pending), and a final status=completed
    // tool_call_update whose content is EMPTY (toolUpdateFromToolResult returns {} for Edit/Write).
    // Counting only ran inside the loop over the completed update's content, so it never ran at
    // all: every row stayed at "+0 -0" (issue #22 item 6, reproduced live in the VS Exp instance).
    [Fact]
    public async Task ToolCallDiff_CompletedUpdateWithoutContent_StillCountsTheDiffReportedEarlier()
    {
        using var workspace = new TempWorkspace();
        var created = workspace.PathUnder("Created.txt");
        var edited = workspace.PathUnder("Modify.txt");
        File.WriteAllText(edited, "line one\nline two\nline three\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "change files";
        var sending = vm.SendAsync();

        // Write: optimistic tool_call (oldText null), agent writes, hook diff (no status), completed (no content).
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.txt", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = created, OldText = null, NewText = "alpha\nbeta\ngamma\ndelta\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        File.WriteAllText(created, "alpha\nbeta\ngamma\ndelta\n");
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.txt", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = created, OldText = "", NewText = "alpha\nbeta\ngamma\ndelta\n" }],
        }));
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.txt", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed, Content = [],
        }));

        // Edit: optimistic old_string/new_string, agent edits, hook diff (no status), completed (no content).
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Modify.txt", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = edited, OldText = "line two", NewText = "line TWO changed" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 2);
        File.WriteAllText(edited, "line one\nline TWO changed\nline three\n");
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Modify.txt", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = edited, OldText = "line one\nline two\nline three\n", NewText = "line one\nline TWO changed\nline three\n" }],
        }));
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Modify.txt", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed, Content = [],
        }));

        await WaitUntilAsync(() => vm.ChangedFiles.All(file => file.AddedLines > 0));
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var createdRow = Assert.Single(vm.ChangedFiles, file => file.Name == "Created.txt");
        Assert.True(createdRow.IsNew);
        Assert.Equal(4, createdRow.AddedLines);
        Assert.Equal(0, createdRow.RemovedLines);
        var editedRow = Assert.Single(vm.ChangedFiles, file => file.Name == "Modify.txt");
        Assert.False(editedRow.IsNew);
        Assert.Equal(1, editedRow.AddedLines);
        Assert.Equal(1, editedRow.RemovedLines);
        Assert.True(editedRow.CanRevert);
    }

    // The same three-notification shape for a denied Edit: the row went in on the optimistic
    // tool_call, the failed update carries only the "Permission denied" text - with nothing to
    // iterate, the row was never taken back and a file the agent never touched stayed listed.
    [Fact]
    public async Task ToolCallDiff_FailedUpdateWithoutContent_UntracksTheUntouchedFile()
    {
        using var workspace = new TempWorkspace();
        var edited = workspace.PathUnder("Modify.txt");
        File.WriteAllText(edited, "line one\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "change it";
        var sending = vm.SendAsync();

        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Modify.txt", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = edited, OldText = "line one", NewText = "line ONE" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Modify.txt", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Failed,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Text = "Permission denied" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 0);

        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;
        Assert.Empty(vm.ChangedFiles);
        Assert.Equal("line one\n", File.ReadAllText(edited));
    }

    // The client cannot tell "the agent created this file" from "the agent rewrote an existing file
    // and omitted oldText": both arrive as an absent oldText over content that already matches the
    // disk. Creation is therefore decided by the client's own read, and a write that landed before
    // its notification is left in place rather than deleted - and, its pre-write content being
    // unknown, offered without a revert - a file left behind is recoverable, the user's file is not.
    [Fact]
    public async Task ToolCallDiff_ForAFileWhoseWriteLandedFirst_RejectLeavesItRatherThanGuessingItWasCreated()
    {
        using var workspace = new TempWorkspace();
        var created = workspace.PathUnder("Created.cs");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "add a file";
        var sending = vm.SendAsync();

        File.WriteAllText(created, "brand new\n"); // the agent created it before telling us
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = created, OldText = null, NewText = "brand new\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.False(file.IsNew);
        Assert.False(file.CanRevert);
        await file.RejectCommand.ExecuteAsync(null);

        Assert.True(File.Exists(created));
        Assert.Equal("brand new\n", File.ReadAllText(created));
    }

    // oldText is agent-controlled and adapters routinely omit it. Reading an absent oldText as
    // "the agent created this file" let a diff whose newText already matches the disk turn a
    // pre-existing file into a "New" row whose Reject - or Reject all - called File.Delete on it.
    [Fact]
    public async Task ToolCallDiff_WithoutOldTextOverAnUnchangedExistingFile_NeverDeletesItOnReject()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Existing.cs");
        File.WriteAllText(targetPath, "the user's work\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "look at it";
        var sending = vm.SendAsync();

        // newText is the file's exact current content and oldText is absent: the agent need not
        // have written anything at all to produce this.
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Existing.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = null, NewText = "the user's work\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.False(file.IsNew);

        await vm.RejectAllChangesCommand.ExecuteAsync(null);

        Assert.True(File.Exists(targetPath));
        Assert.Equal("the user's work\n", File.ReadAllText(targetPath));
    }

    // "File-system work (path canonicalization, whole-file reads) must never run on the WPF
    // dispatcher." Every entry point of the changed-file ledger does it: the pre-edit snapshot,
    // the open (which re-resolves the tracked path), and the revert - which "Reject all" repeats
    // once per file, back to back, straight off a click.
    [Fact]
    public async Task ChangedFileLedger_DoesItsFileWorkOffTheDispatcher()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Tracked.cs");
        File.WriteAllText(targetPath, "before\n");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var dispatcher = SynchronizationContext.Current;
        Assert.NotNull(dispatcher);
        services.OpenDocuments[Path.GetFullPath(targetPath)] = "before\n";
        var snapshotContexts = new ConcurrentBag<SynchronizationContext?>();
        var openContexts = new ConcurrentBag<SynchronizationContext?>();
        var revertContexts = new ConcurrentBag<SynchronizationContext?>();
        services.OpenDocumentHandler = (_, _) =>
        {
            openContexts.Add(SynchronizationContext.Current);
            return Task.CompletedTask;
        };
        services.ReadOpenDocumentHandler = (_, _) =>
        {
            snapshotContexts.Add(SynchronizationContext.Current);
            return Task.FromResult<string?>("before\n");
        };
        services.WriteOpenDocumentHandler = (path, text, _) =>
        {
            revertContexts.Add(SynchronizationContext.Current);
            services.OpenDocuments[path] = text;
            return Task.FromResult(true);
        };

        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "edit it";
        var sending = vm.SendAsync();
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Tracked.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "before\n", NewText = "after\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        // A WPF command runs with the dispatcher's context installed; that is the condition under
        // which a synchronous lease acquisition plus file write is a visible devenv freeze.
        await WithDispatcherInstalled(dispatcher!, () => vm.OpenChangedFileCommand.ExecuteAsync(vm.ChangedFiles[0]));
        await WithDispatcherInstalled(dispatcher!, () => vm.RejectAllChangesCommand.ExecuteAsync(null));

        Assert.Null(vm.StatusMessage);
        Assert.NotEmpty(snapshotContexts);
        Assert.All(snapshotContexts, context => Assert.NotSame(dispatcher, context));
        Assert.NotEmpty(openContexts);
        Assert.All(openContexts, context => Assert.NotSame(dispatcher, context));
        Assert.NotEmpty(revertContexts);
        Assert.All(revertContexts, context => Assert.NotSame(dispatcher, context));
    }

    private static Task WithDispatcherInstalled(SynchronizationContext dispatcher, Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(dispatcher);
        try { return action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task AcceptAllAndRejectAllChanges_AreDisabledWhenThereIsNothingToApply()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("a.txt");
        File.WriteAllText(targetPath, "A0");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        Assert.False(vm.AcceptAllChangesCommand.CanExecute(null));
        Assert.False(vm.RejectAllChangesCommand.CanExecute(null));

        Assert.True(await connection.RaiseFileWriteRequested(targetPath, "A1").Response.Task);
        Assert.True(vm.AcceptAllChangesCommand.CanExecute(null));
        Assert.True(vm.RejectAllChangesCommand.CanExecute(null));

        await vm.RejectAllChangesCommand.ExecuteAsync(null);
        Assert.False(vm.AcceptAllChangesCommand.CanExecute(null));
        Assert.False(vm.RejectAllChangesCommand.CanExecute(null));
    }

    // Per-file failures overwrite one another's status message, so "Reject all" over ten files with
    // three failures used to name exactly one of them while the other rows stayed in the panel.
    [Fact]
    public async Task RejectAllChanges_ReportsHowManyFilesFailed_AndKeepsTheirRows()
    {
        using var workspace = new TempWorkspace();
        var first = workspace.PathUnder("a.txt");
        var second = workspace.PathUnder("b.txt");
        File.WriteAllText(first, "A0");
        File.WriteAllText(second, "B0");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        Assert.True(await connection.RaiseFileWriteRequested(first, "A1").Response.Task);
        Assert.True(await connection.RaiseFileWriteRequested(second, "B1").Response.Task);

        // The failure comes from the handler alone: the stub consults it before OpenDocuments, and
        // what gets the revert as far as the handler is the lease's document pin, not an entry here.
        services.WriteOpenDocumentHandler = (_, _, _) => throw new UnauthorizedAccessException("the buffer is read-only");

        await vm.RejectAllChangesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.ChangedFiles.Count);
        Assert.Contains("2 of 2", vm.StatusMessage!, StringComparison.Ordinal);
    }

    // AsyncRelayCommand rethrows a faulted task on the UI thread, and the host's open genuinely
    // fails for a file renamed or deleted after it was tracked - Reject itself deletes files.
    [Fact]
    public async Task OpenChangedFileCommand_WhenTheHostCannotOpenTheFile_ReportsItWithoutFaulting()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("gone.cs");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        Assert.True(await connection.RaiseFileWriteRequested(targetPath, "x").Response.Task);
        services.OpenDocumentHandler = (_, _) => throw new FileNotFoundException("The document was renamed.");

        await vm.OpenChangedFileCommand.ExecuteAsync(vm.ChangedFiles[0]);

        Assert.Contains("gone.cs", vm.StatusMessage!, StringComparison.Ordinal);
    }

    // Only a document lease makes FullPath safe for a path-based host API (WorkspacePathLease):
    // the leaf has to stay pinned while VS opens it, exactly as the read and write paths pin it.
    // A file gone since it was tracked therefore never reaches the host as a bare path.
    [Fact]
    public async Task OpenChangedFileCommand_ForAFileGoneSinceItWasTracked_DoesNotHandTheHostAnUnpinnedPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("gone.cs");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        Assert.True(await connection.RaiseFileWriteRequested(targetPath, "x").Response.Task);
        File.Delete(targetPath);

        await vm.OpenChangedFileCommand.ExecuteAsync(vm.ChangedFiles[0]);

        Assert.Empty(services.OpenedDocumentPaths);
        Assert.Contains("gone.cs", vm.StatusMessage!, StringComparison.Ordinal);
    }

    // The row goes in before the write so the pre-write snapshot is taken first; a write that
    // then never lands (the editor rejects the edit, a disk error) leaves nothing to list, while a
    // row created by an earlier write that did land keeps offering to revert it.
    [Fact]
    public async Task FileWriteRequest_ThatFails_KeepsOnlyRowsForWritesThatLanded()
    {
        using var workspace = new TempWorkspace();
        var landed = workspace.PathUnder("landed.cs");
        var rejected = workspace.PathUnder("rejected.cs");
        File.WriteAllText(landed, "original");
        File.WriteAllText(rejected, "original");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        services.OpenDocuments[Path.GetFullPath(landed)] = "original";
        services.OpenDocuments[Path.GetFullPath(rejected)] = "original";
        Assert.True(await connection.RaiseFileWriteRequested(landed, "changed").Response.Task);
        services.WriteOpenDocumentHandler = (_, _, _) => Task.FromException<bool>(new IOException("The editor rejected the edit."));

        await Assert.ThrowsAsync<IOException>(() => connection.RaiseFileWriteRequested(landed, "changed again").Response.Task);
        await Assert.ThrowsAsync<IOException>(() => connection.RaiseFileWriteRequested(rejected, "changed").Response.Task);

        Assert.Equal("landed.cs", Assert.Single(vm.ChangedFiles).Name);
    }
}
