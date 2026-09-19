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
        File.WriteAllText(targetPath, "line a\nline b\n");
        var (vm, connection, _) = await ConnectWithWorkspaceAsync(workspace.Root);
        using var _vm = vm;

        Assert.True(await connection.RaiseFileWriteRequested(targetPath, "line a\nline c\nline d\n").Response.Task);
        Assert.True(await connection.RaiseFileWriteRequested(targetPath, "line a\nline c\nline d\nline e\n").Response.Task);

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
            return Task.FromResult(new ClaudeCode.Contracts.NewSessionResult(sessionId, []));
        };

        await vm.OpenSessionCommand.ExecuteAsync(new ClaudeCode.Contracts.SessionSummary("old", workspace.Root, "Old chat", null));
        await Task.Delay(50);

        Assert.Empty(vm.ChangedFiles);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
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
}
