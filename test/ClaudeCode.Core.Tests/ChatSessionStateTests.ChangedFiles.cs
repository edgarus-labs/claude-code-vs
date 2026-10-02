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
        Assert.Equal("A2", File.ReadAllText(first));
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
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "edit it";
        var sending = vm.SendAsync();

        var pending = new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Edited.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "before\n", NewText = "after\nmore\n" }],
        };
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(pending));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);

        File.WriteAllText(targetPath, "after\nmore\n");
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
        Assert.True(condition(), "timed out waiting for the expected state");
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

        File.WriteAllText(targetPath, "after\nmore\n");
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

        File.WriteAllText(targetPath, "after\nmore\n");
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
        Assert.Single(vm.ChangedFiles);
        Assert.Contains("1 of 1", vm.StatusMessage!, StringComparison.Ordinal);
    }

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

        File.WriteAllText(targetPath, "before\nmore\n");
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
        connection.NewSessionHandler = _ => Task.FromResult(new ClaudeCode.Contracts.NewSessionResult("session-2", []));
        await WithDispatcherInstalled(ui, () => vm.NewSessionAsync());
        ui.Drain();

        Assert.Empty(vm.ChangedFiles);
        vm.Dispose();
        ui.Drain();
    }

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

        File.WriteAllText(targetPath, "brand new\nfile\n");
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

    [Fact]
    public async Task ToolCallDiff_RejectRacingTheEmptySnapshotCorrection_DoesNotTruncateTheCreatedFile()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Created.cs");
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
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "create it";
        var sending = vm.SendAsync();
        while (!vm.IsBusy) { ui.Drain(); await Task.Yield(); }

        File.WriteAllText(targetPath, "brand new\nfile\n");
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = null, NewText = "brand new\nfile" }],
        }));
        while (vm.ChangedFiles.Count == 0) { ui.Drain(); await Task.Yield(); }
        var file = vm.ChangedFiles[0];
        Assert.False(file.IsNew);
        Assert.True(file.CanRevert);

        await WithDispatcherInstalled(ui, () =>
        {
            connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
            {
                ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
                Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "", NewText = "brand new\nfile\n" }],
            }));
            return Task.CompletedTask;
        });
        await WaitUntilAsync(() => file.OriginalText is { Length: 0 });

        var rejecting = WithDispatcherInstalled(ui, () => file.RejectCommand.ExecuteAsync(null));
        while (!rejecting.IsCompleted) { ui.Drain(); await Task.Yield(); }
        await rejecting;

        Assert.True(File.Exists(targetPath));
        Assert.Equal("brand new\nfile\n", File.ReadAllText(targetPath));
        Assert.False(file.CanRevert);

        turn.SetResult(true);
        while (!sending.IsCompleted) { ui.Drain(); await Task.Yield(); }
        await sending;
        vm.Dispose();
        ui.Drain();
    }

    [Fact]
    public async Task ToolCallDiff_NewCrlfFileSnapshotTakenAfterTheWriteLanded_ShowsItsRealAdditions()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Created.cs");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "create it";
        var sending = vm.SendAsync();

        File.WriteAllText(targetPath, "brand new\r\nfile\r\n");
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Pending,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = null, NewText = "brand new\nfile\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);

        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
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

    [Fact]
    public async Task ToolCallDiff_CompletedUpdateFindingTheRowCreatedWhileItRead_StillCorrectsTheSnapshot()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("Created.cs");
        File.WriteAllText(targetPath, "brand new\nfile\n");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "create it";
        var sending = vm.SendAsync();

        var reads = 0;
        var completedIsReading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        services.ReadOpenDocumentHandler = async (_, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                completedIsReading.SetResult(true);
                await resumeCompleted.Task;
            }

            return null;
        };

        ClaudeCode.Contracts.ToolCallUpdate Update(ClaudeCode.Contracts.ToolCallStatus status, string? oldText) => new()
        {
            ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = status,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = oldText, NewText = "brand new\nfile\n" }],
        };
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update(ClaudeCode.Contracts.ToolCallStatus.Completed, "")));
        await completedIsReading.Task;
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update(ClaudeCode.Contracts.ToolCallStatus.Pending, null)));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        resumeCompleted.SetResult(true);

        await WaitUntilAsync(() => Volatile.Read(ref reads) == 3);
        var file = Assert.Single(vm.ChangedFiles);
        Assert.Equal("", file.OriginalText);
        await WaitUntilAsync(() => file.AddedLines == 2);
        Assert.Equal(0, file.RemovedLines);

        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;
    }

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

    private static ClaudeCode.Contracts.SessionUpdate.ToolCall EditCall(string id, string path, ClaudeCode.Contracts.ToolCallStatus status, string? oldText, string? newText) =>
        new(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = id, Title = "Edit " + Path.GetFileName(path), Kind = "edit", Status = status,
            Content = newText is null ? [] : [new ClaudeCode.Contracts.ToolCallContent { Path = path, OldText = oldText, NewText = newText }],
        });

    [Fact]
    public async Task ToolCallDiff_ALaterCallWritingTheOriginalBack_DoesNotRewriteTheRowsSnapshot()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathUnder("Round.txt");
        File.WriteAllText(path, "A\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "round trip";
        var sending = vm.SendAsync();

        connection.RaiseSessionUpdate(EditCall("edit-1", path, ClaudeCode.Contracts.ToolCallStatus.Pending, "A", "B"));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        File.WriteAllText(path, "B\n");
        connection.RaiseSessionUpdate(EditCall("edit-1", path, ClaudeCode.Contracts.ToolCallStatus.Pending, "A\n", "B\n"));
        connection.RaiseSessionUpdate(EditCall("edit-1", path, ClaudeCode.Contracts.ToolCallStatus.Completed, null, null));
        await WaitUntilAsync(() => vm.ChangedFiles[0].AddedLines == 1);

        connection.RaiseSessionUpdate(EditCall("edit-2", path, ClaudeCode.Contracts.ToolCallStatus.Pending, "B", "A"));
        File.WriteAllText(path, "A\n");
        connection.RaiseSessionUpdate(EditCall("edit-2", path, ClaudeCode.Contracts.ToolCallStatus.Pending, "B\n", "A\n"));
        connection.RaiseSessionUpdate(EditCall("edit-2", path, ClaudeCode.Contracts.ToolCallStatus.Completed, null, null));
        await WaitUntilAsync(() => vm.ChangedFiles[0].AddedLines == 0);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.Equal("A\n", file.OriginalText);
        Assert.Equal(0, file.RemovedLines);
    }

    [Fact]
    public async Task ToolCallDiff_CorrectedSnapshotOfARevertableRow_KeepsRejectAvailable()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.PathUnder("Insert.txt");
        File.WriteAllText(path, "one\ninserted\ntwo\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "insert";
        var sending = vm.SendAsync();

        connection.RaiseSessionUpdate(EditCall("edit-1", path, ClaudeCode.Contracts.ToolCallStatus.Pending, "one", "one\ninserted"));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(EditCall("edit-1", path, ClaudeCode.Contracts.ToolCallStatus.Pending, "one\ntwo\n", "one\ninserted\ntwo\n"));
        connection.RaiseSessionUpdate(EditCall("edit-1", path, ClaudeCode.Contracts.ToolCallStatus.Completed, null, null));
        await WaitUntilAsync(() => vm.ChangedFiles[0].AddedLines == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.True(file.CanRevert);
        await file.RejectCommand.ExecuteAsync(null);
        Assert.Equal("one\ntwo\n", File.ReadAllText(path));
    }

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

        File.WriteAllText(created, "brand new\n");
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

    [Fact]
    public async Task ToolCallDiff_CorrectedToAnEmptyOriginal_OffersNoRevertToTruncateTheCreatedFileWith()
    {
        using var workspace = new TempWorkspace();
        var created = workspace.PathUnder("Created.cs");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "add a file";
        var sending = vm.SendAsync();

        File.WriteAllText(created, "brand new\nfile\n");
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = created, OldText = "", NewText = "brand new\nfile\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.False(file.CanRevert);
        await vm.RejectAllChangesCommand.ExecuteAsync(null);

        Assert.Equal("brand new\nfile\n", File.ReadAllText(created));
        Assert.Single(vm.ChangedFiles);
    }

    [Fact]
    public async Task ToolCallDiff_ExistingRowCorrectedToAnEmptyOriginal_WithdrawsItsRevert()
    {
        using var workspace = new TempWorkspace();
        var created = workspace.PathUnder("Created.cs");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        var turn = new TaskCompletionSource<bool>();
        connection.PromptHandler = _ => turn.Task;
        vm.InputText = "add a file";
        var sending = vm.SendAsync();

        File.WriteAllText(created, "brand new\nfile\n");
        ClaudeCode.Contracts.ToolCallUpdate Update(ClaudeCode.Contracts.ToolCallStatus status) => new()
        {
            ToolCallId = "write-1", Title = "Write Created.cs", Kind = "edit", Status = status,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = created, OldText = "", NewText = "brand new\nfile\n" }],
        };
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update(ClaudeCode.Contracts.ToolCallStatus.Pending)));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        Assert.True(vm.ChangedFiles[0].CanRevert);

        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(Update(ClaudeCode.Contracts.ToolCallStatus.Completed)));
        await WaitUntilAsync(() => !vm.ChangedFiles[0].CanRevert);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.Equal("", file.OriginalText);
        await WaitUntilAsync(() => file.AddedLines == 2);
        await file.RejectCommand.ExecuteAsync(null);

        Assert.Equal("brand new\nfile\n", File.ReadAllText(created));
    }

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
        services.OpenDocumentHandler = (_, _, _) =>
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

        services.WriteOpenDocumentHandler = (_, _, _) => throw new UnauthorizedAccessException("the buffer is read-only");

        await vm.RejectAllChangesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.ChangedFiles.Count);
        Assert.Contains("2 of 2", vm.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenChangedFileCommand_WhenTheHostCannotOpenTheFile_ReportsItWithoutFaulting()
    {
        using var workspace = new TempWorkspace();
        var targetPath = workspace.PathUnder("gone.cs");
        var (vm, connection, services) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;
        Assert.True(await connection.RaiseFileWriteRequested(targetPath, "x").Response.Task);
        services.OpenDocumentHandler = (_, _, _) => throw new FileNotFoundException("The document was renamed.");

        await vm.OpenChangedFileCommand.ExecuteAsync(vm.ChangedFiles[0]);

        Assert.Contains("gone.cs", vm.StatusMessage!, StringComparison.Ordinal);
    }

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
