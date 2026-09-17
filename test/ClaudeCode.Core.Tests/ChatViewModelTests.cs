using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using ClaudeCode.Core.ViewModels.Demo;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChatViewModelTests
{
    [Fact]
    public async Task SendAsync_WithDemoFakeConnection_ProducesAssistantMessage()
    {
        var vm = new ChatViewModel(new NullChatSessionServices());
        await vm.InitializeAsync();

        vm.InputText = "hello there";
        await vm.SendAsync();

        var assistantMessage = Assert.Single(vm.Messages, m => m.Role == ChatRole.Assistant);
        Assert.Contains("hello there", assistantMessage.Text);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task PlanSessionUpdate_PopulatesCurrentPlanEntries()
    {
        var connection = new RecordingAcpAgentConnection();
        var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "make a plan";
        await vm.SendAsync();

        var entries = new List<PlanEntry>
        {
            new PlanEntry { Content = "Write tests", Status = PlanEntryStatus.Pending },
            new PlanEntry { Content = "Implement feature", Status = PlanEntryStatus.InProgress },
        };
        connection.RaiseSessionUpdate(new SessionUpdate.Plan(entries));

        Assert.NotNull(vm.CurrentPlan);
        Assert.Equal(2, vm.CurrentPlan!.Entries.Count);
        Assert.Equal("Write tests", vm.CurrentPlan.Entries[0].Content);
    }

    [Fact]
    public async Task PermissionRequested_PopulatesPendingPermission_AndChooseCommandCompletesResponse()
    {
        var connection = new RecordingAcpAgentConnection();
        var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "edit a file for me";
        await vm.SendAsync();

        var call = new ToolCallUpdate { ToolCallId = "tc-1", Title = "Edit file.txt", Status = ToolCallStatus.Pending };
        var options = new List<PermissionOption>
        {
            new PermissionOption { OptionId = "allow-once", Label = "Allow", Outcome = PermissionOutcome.AllowOnce },
            new PermissionOption { OptionId = "reject-once", Label = "Reject", Outcome = PermissionOutcome.RejectOnce },
        };

        var requestArgs = connection.RaisePermissionRequested(call, options);

        Assert.NotNull(vm.PendingPermission);
        var chosenOption = vm.PendingPermission!.Options[0];
        vm.PendingPermission.ChooseCommand.Execute(chosenOption);

        var resultOptionId = await requestArgs.Response.Task;

        Assert.Equal("allow-once", resultOptionId);
        Assert.Null(vm.PendingPermission);
    }
}
