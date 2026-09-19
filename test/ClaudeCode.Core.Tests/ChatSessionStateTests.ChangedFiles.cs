using System;
using System.IO;
using System.Linq;
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
    // already changed. Snapshotting the file at that moment makes Reject write the edit back over
    // itself and report success.
    [Fact]
    public async Task ToolCallDiff_FirstSeenAfterTheAgentWroteTheFile_StillRestoresThePreEditContent()
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

        File.WriteAllText(targetPath, "after\nmore\n"); // the write lands before the notification
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.ToolCall(new ClaudeCode.Contracts.ToolCallUpdate
        {
            ToolCallId = "edit-1", Title = "Edit Edited.cs", Kind = "edit", Status = ClaudeCode.Contracts.ToolCallStatus.Completed,
            Content = [new ClaudeCode.Contracts.ToolCallContent { Path = targetPath, OldText = "before\n", NewText = "after\nmore\n" }],
        }));
        await WaitUntilAsync(() => vm.ChangedFiles.Count == 1);
        connection.RaiseSessionUpdate(new ClaudeCode.Contracts.SessionUpdate.TurnEnded("end_turn"));
        turn.SetResult(true);
        await sending;

        var file = Assert.Single(vm.ChangedFiles);
        Assert.False(file.IsNew);
        await file.RejectCommand.ExecuteAsync(null);

        Assert.Equal("before\n", File.ReadAllText(targetPath));
        Assert.Empty(vm.ChangedFiles);
    }

    [Fact]
    public async Task ToolCallDiff_ForAFileTheAgentCreated_RejectDeletesItEvenWhenTheWriteLandedFirst()
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
        Assert.True(file.IsNew);
        await file.RejectCommand.ExecuteAsync(null);

        Assert.False(File.Exists(created));
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

        services.OpenDocuments[Path.GetFullPath(first)] = "A1";
        services.OpenDocuments[Path.GetFullPath(second)] = "B1";
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
}
