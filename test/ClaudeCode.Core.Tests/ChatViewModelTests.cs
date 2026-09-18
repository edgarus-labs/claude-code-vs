using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChatViewModelTests
{
    // F-7-15: NullChatSessionServices + FakeAcpAgentConnection/FakeAcpAgentConnectionFactory (in
    // ViewModels/Demo/) are a live production fallback (ChatPanelView.xaml.cs falls back to
    // NullChatSessionServices when no host-provided IChatSessionServices is available) that had no
    // test coverage. This asserts the combination is wired correctly end to end: sending a prompt
    // produces a visible assistant reply. It intentionally does not assert the exact echo wording,
    // which is an implementation detail of the demo double, not an observable contract.
    [Fact]
    public async Task SendAsync_WithNullChatSessionServices_ProducesAssistantMessage()
    {
        using var vm = new ChatViewModel(new ClaudeCode.Core.ViewModels.Demo.NullChatSessionServices());
        await vm.InitializeAsync();

        vm.InputText = "hello there";
        await vm.SendAsync();

        var assistantMessage = Assert.Single(vm.Messages, m => m.Role == ChatRole.Assistant);
        Assert.False(string.IsNullOrWhiteSpace(assistantMessage.Text));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task PlanSessionUpdate_ReplacesPreviousPlan_AndReleaseConnectionClearsIt()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.InitializeAsync();

        vm.InputText = "make a plan";
        await vm.SendAsync();

        connection.RaiseSessionUpdate(new SessionUpdate.Plan(new List<PlanEntry>
        {
            new PlanEntry { Content = "Write tests", Status = PlanEntryStatus.Pending },
        }));
        Assert.Equal("Write tests", Assert.Single(vm.CurrentPlan!.Entries).Content);

        // A later plan update replaces the prior one wholesale; it does not merge/append entries.
        connection.RaiseSessionUpdate(new SessionUpdate.Plan(new List<PlanEntry>
        {
            new PlanEntry { Content = "Ship feature", Status = PlanEntryStatus.InProgress },
        }));
        Assert.Equal("Ship feature", Assert.Single(vm.CurrentPlan!.Entries).Content);

        connection.RaiseDisconnected();
        Assert.Null(vm.CurrentPlan);
    }

    [Fact]
    public async Task PermissionRequested_ChoosingSecondOption_RespondsWithSecondOptionId()
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
        var secondOption = vm.PendingPermission!.Options[1];
        vm.PendingPermission.ChooseCommand.Execute(secondOption);

        var resultOptionId = await requestArgs.Response.Task;

        Assert.Equal("reject-once", resultOptionId);
        Assert.Null(vm.PendingPermission);
    }
}
