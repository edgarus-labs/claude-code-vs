using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed partial class ChatSessionStateTests
{
    [Fact]
    public async Task AttachActiveDocument_CapturesEachTabAtClick_AndSendsSlashTextBeforeOrderedResourcesAndImage()
    {
        var connection = new RecordingAcpAgentConnection();
        var active = new EditorDocumentSnapshot(@"C:\Workspace\space # ż.cs", "unsaved first tab");
        var captures = 0;
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            CaptureHandler = _ =>
            {
                captures++;

                return Task.FromResult<EditorDocumentSnapshot?>(active);
            },
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        vm.AddImageAttachment("screen.png", "image/png", "AQID");
        active = new EditorDocumentSnapshot(@"C:\Workspace\second.cs", "unsaved second tab");
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        active = new EditorDocumentSnapshot(@"C:\Workspace\third.cs", "must not be captured at send");
        vm.InputText = "/review explain these changes";

        Assert.Collection(vm.Attachments,
            attachment => { Assert.Equal("space # ż.cs", attachment.Name); Assert.True(attachment.IsDocument); Assert.False(attachment.IsImage); },
            attachment => { Assert.True(attachment.IsImage); Assert.False(attachment.IsDocument); },
            attachment => Assert.Equal("second.cs", attachment.Name));
        await vm.SendAsync();

        Assert.Equal(2, captures);
        Assert.Collection(Assert.Single(connection.Prompts),
            block => Assert.Equal("/review explain these changes", Assert.IsType<ContentBlock.Text>(block).Value),
            block =>
            {
                var resource = Assert.IsType<ContentBlock.EmbeddedTextResource>(block);
                Assert.Equal("file:///C:/Workspace/space%20%23%20%C5%BC.cs", resource.Uri);
                Assert.Equal("unsaved first tab", resource.Text);
            },
            block =>
            {
                var image = Assert.IsType<ContentBlock.Image>(block);
                Assert.Equal("image/png", image.MimeType);
                Assert.Equal("AQID", image.Base64Data);
            },
            block =>
            {
                var resource = Assert.IsType<ContentBlock.EmbeddedTextResource>(block);
                Assert.Equal("file:///C:/Workspace/second.cs", resource.Uri);
                Assert.Equal("unsaved second tab", resource.Text);
            });
        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public async Task AttachActiveDocument_CanonicalCaseInsensitivePath_ReplacesSnapshotInOriginalPosition()
    {
        var connection = new RecordingAcpAgentConnection();
        var active = new EditorDocumentSnapshot(@"C:\Workspace\nested\..\first.cs", "old snapshot");
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            CaptureHandler = _ => Task.FromResult<EditorDocumentSnapshot?>(active),
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        vm.AddImageAttachment("between.png", "image/png", "AQID");
        active = new EditorDocumentSnapshot(@"C:\Workspace\second.cs", "second snapshot");
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        active = new EditorDocumentSnapshot("c:/workspace/FIRST.cs", "replacement snapshot");
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);

        Assert.True(vm.SendCommand.CanExecute(null));
        await vm.SendAsync();

        Assert.Collection(Assert.Single(connection.Prompts),
            block => Assert.Equal("replacement snapshot", Assert.IsType<ContentBlock.EmbeddedTextResource>(block).Text),
            block => Assert.Equal("AQID", Assert.IsType<ContentBlock.Image>(block).Base64Data),
            block => Assert.Equal("second snapshot", Assert.IsType<ContentBlock.EmbeddedTextResource>(block).Text));
    }

    [Fact]
    public async Task DocumentOnlyDraft_CanBeRemovedOrSentAsCapturedResource()
    {
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            CaptureHandler = _ => Task.FromResult<EditorDocumentSnapshot?>(new EditorDocumentSnapshot(@"C:\Workspace\only.cs", "unsaved document")),
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        vm.RemoveAttachmentCommand.Execute(Assert.Single(vm.Attachments));
        Assert.False(vm.SendCommand.CanExecute(null));
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        Assert.True(vm.SendCommand.CanExecute(null));

        await vm.SendAsync();

        var resource = Assert.IsType<ContentBlock.EmbeddedTextResource>(Assert.Single(Assert.Single(connection.Prompts)));
        Assert.Equal("file:///C:/Workspace/only.cs", resource.Uri);
        Assert.Equal("unsaved document", resource.Text);
        Assert.Empty(vm.Attachments);
        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachActiveDocument_NoEditorOrCaptureFailure_PreservesDraftAndAllowsRetry(bool fails)
    {
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService());
        if (fails)
        {
            services.CaptureHandler = _ => Task.FromException<EditorDocumentSnapshot?>(new InvalidOperationException("Editor unavailable"));
        }

        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "keep this draft";
        vm.AddImageAttachment("keep.png", "image/png", "AQID");
        var image = Assert.Single(vm.Attachments);

        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);

        Assert.Equal("keep this draft", vm.InputText);
        Assert.Same(image, Assert.Single(vm.Attachments));
        Assert.False(string.IsNullOrWhiteSpace(vm.AttachmentError));
        Assert.Empty(connection.Prompts);
        services.CaptureHandler = _ => Task.FromResult<EditorDocumentSnapshot?>(new EditorDocumentSnapshot(@"C:\Workspace\retry.cs", "recovered"));
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        Assert.Null(vm.AttachmentError);
        Assert.Contains(vm.Attachments, attachment => attachment.Name == "retry.cs" && attachment.IsDocument);
    }

    [Fact]
    public async Task DisposeDuringCapture_DiscardsLateSnapshotWithoutChangingDraft()
    {
        var captured = new TaskCompletionSource<EditorDocumentSnapshot?>();
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            CaptureHandler = _ => captured.Task,
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "preserved draft";
        vm.AddImageAttachment("keep.png", "image/png", "AQID");
        var image = Assert.Single(vm.Attachments);
        var capture = vm.AttachActiveDocumentCommand.ExecuteAsync(null);

        vm.Dispose();
        captured.SetResult(new EditorDocumentSnapshot(@"C:\Workspace\late.cs", "late snapshot"));
        await capture;

        Assert.Equal("preserved draft", vm.InputText);
        Assert.Same(image, Assert.Single(vm.Attachments));
        Assert.False(vm.AttachActiveDocumentCommand.CanExecute(null));
        Assert.Empty(connection.Prompts);
    }

    [Fact]
    public async Task PendingCapture_BlocksSendAndConfiguration_UntilSnapshotCanJoinDraft()
    {
        var captured = new TaskCompletionSource<EditorDocumentSnapshot?>();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            CaptureHandler = _ => captured.Task,
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "review captured file";
        var capture = vm.AttachActiveDocumentCommand.ExecuteAsync(null);

        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.False(vm.CanConfigure);
        await vm.SendAsync();
        await vm.SelectModelAsync(vm.AvailableModels[1]);
        Assert.Empty(connection.Prompts);
        Assert.Empty(connection.ConfigChanges);
        Assert.Equal("review captured file", vm.InputText);
        captured.SetResult(new EditorDocumentSnapshot(@"C:\Workspace\captured.cs", "captured before send"));
        await capture;

        Assert.True(vm.SendCommand.CanExecute(null));
        Assert.True(vm.CanConfigure);
        await vm.SendAsync();
        Assert.Collection(Assert.Single(connection.Prompts),
            block => Assert.Equal("review captured file", Assert.IsType<ContentBlock.Text>(block).Value),
            block => Assert.Equal("captured before send", Assert.IsType<ContentBlock.EmbeddedTextResource>(block).Text));
    }

    [Theory]
    [InlineData("connecting")]
    [InlineData("signed-out")]
    [InlineData("configuring")]
    [InlineData("disposed")]
    public async Task DraftGates_BlockCaptureImageMutationAndSend(string gate)
    {
        var ready = new TaskCompletionSource<NewSessionResult>();
        var config = new TaskCompletionSource<IReadOnlyList<SessionConfigOption>>();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        if (gate == "connecting")
        {
            connection.NewSessionHandler = _ => ready.Task;
        }

        if (gate == "configuring")
        {
            connection.ConfigHandler = (_, _, _) => config.Task;
        }

        var captures = 0;
        IAcpAuthService auth = gate == "signed-out" ? new AdvisoryAuthService(AuthState.SignedOut) : new AlwaysSignedInAuthService();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), auth)
        {
            CaptureHandler = _ =>
            {
                captures++;

                return Task.FromResult<EditorDocumentSnapshot?>(new EditorDocumentSnapshot(@"C:\Workspace\blocked.cs", "blocked"));
            },
        };
        using var vm = new ChatViewModel(services);
        Task pending = Task.CompletedTask;
        if (gate != "connecting")
        {
            await vm.Initialization;
        }

        if (gate is "configuring" or "disposed")
        {
            vm.AddImageAttachment("retained.png", "image/png", "AQID");
            if (gate == "configuring")
            {
                pending = vm.SelectModelAsync(vm.AvailableModels[1]);
            }

            if (gate == "disposed")
            {
                vm.Dispose();
            }
        }
        vm.InputText = "/co";
        var attachments = vm.Attachments.ToArray();
        var promptCount = connection.Prompts.Count;

        Assert.False(vm.AttachActiveDocumentCommand.CanExecute(null));
        Assert.False(vm.SendCommand.CanExecute(null));
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        vm.AddImageAttachment("blocked.png", "image/png", "BAUG");
        foreach (var attachment in attachments)
        {
            Assert.False(vm.RemoveAttachmentCommand.CanExecute(attachment));
            vm.RemoveAttachmentCommand.Execute(attachment);
        }
        await vm.SendAsync();

        Assert.Equal(0, captures);
        Assert.Equal("/co", vm.InputText);
        Assert.Equal(attachments, vm.Attachments);
        Assert.Equal(promptCount, connection.Prompts.Count);
        ready.TrySetResult(new NewSessionResult(RecordingAcpAgentConnection.SessionId, Options()));
        config.TrySetResult(Options("opus"));
        await pending;
        await vm.Initialization;
    }

    [Fact]
    public async Task Busy_BlocksCaptureAndAttachmentMutation_ButQueuesSendUntilTurnEnds()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        var captures = 0;
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            CaptureHandler = _ =>
            {
                captures++;

                return Task.FromResult<EditorDocumentSnapshot?>(new EditorDocumentSnapshot(@"C:\Workspace\blocked.cs", "blocked"));
            },
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "first";
        var firstTurn = vm.SendAsync();
        Assert.True(vm.IsBusy);

        vm.AddImageAttachment("retained.png", "image/png", "AQID");
        var attachments = vm.Attachments.ToArray();

        Assert.False(vm.AttachActiveDocumentCommand.CanExecute(null));
        await vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        Assert.Equal(0, captures);
        foreach (var attachment in attachments)
        {
            Assert.False(vm.RemoveAttachmentCommand.CanExecute(attachment));
            vm.RemoveAttachmentCommand.Execute(attachment);
        }
        Assert.Equal(attachments, vm.Attachments);

        vm.InputText = "second, queued";
        Assert.True(vm.SendCommand.CanExecute(null));
        await vm.SendAsync();

        var queued = Assert.Single(vm.Messages, message => message.Role == ChatRole.User && message.Text == "second, queued");
        Assert.True(queued.IsPending);
        Assert.Equal(string.Empty, vm.InputText);
        Assert.Empty(vm.Attachments);
        Assert.Single(connection.Prompts);

        completed.SetResult(true);
        await firstTurn;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);

        Assert.False(queued.IsPending);
        Assert.Equal("second, queued", Assert.IsType<ContentBlock.Text>(Assert.Single(connection.Prompts[1])).Value);
    }

    [Fact]
    public async Task SendCommand_StaysExecutableWhileTheTurnItStartedIsInFlight_AndQueues()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));
        await vm.Initialization;
        vm.InputText = "first";
        var firstTurn = vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        vm.InputText = "second, queued";
        Assert.True(vm.SendCommand.CanExecute(null));
        await vm.SendCommand.ExecuteAsync(null);

        var queued = Assert.Single(vm.Messages, message => message.Role == ChatRole.User && message.Text == "second, queued");
        Assert.True(queued.IsPending);
        Assert.Single(connection.Prompts);

        completed.SetResult(true);
        await firstTurn;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);
        Assert.False(queued.IsPending);
    }

    [Fact]
    public async Task QueuedMessage_IsNotDispatchedIntoANewWorkspaceIfTheTurnEndsMidSwitch()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var releaseGate = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection
        {
            PromptHandler = _ => firstTurn.Task,
            DisposeHandler = () => releaseGate.Task,
        };
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), workspaceRoot: @"C:\ProjectA");
        using var vm = new ChatViewModel(services);
        await vm.Initialization;

        vm.InputText = "for project A";
        var firstSend = vm.SendAsync();
        Assert.True(vm.IsBusy);
        Assert.Equal(@"C:\ProjectA", Assert.Single(connection.NewSessionCwds));

        vm.InputText = "queued while on project A";
        await vm.SendAsync();
        var queued = Assert.Single(vm.Messages, message => message.Role == ChatRole.User && message.Text == "queued while on project A");
        Assert.True(queued.IsPending);

        services.SetWorkspaceRoot(@"C:\ProjectB");

        firstTurn.SetResult(true);
        await firstSend;
        Assert.True(queued.IsPending);
        Assert.Single(connection.NewSessionCwds);

        releaseGate.SetResult(true);
        await WaitUntilAsync(() => vm.SessionTitle == "Untitled" && !vm.IsBusy);

        Assert.DoesNotContain(connection.Prompts, prompt =>
            prompt.Any(block => block is ContentBlock.Text text && text.Value == "queued while on project A"));
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task AgentDisconnect_DiscardsQueuedMessages_AndSaysSoInsteadOfLeavingThemInTheTranscript()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => firstTurn.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "queued behind the first";
        await vm.SendAsync();
        var queued = Assert.Single(vm.Messages, message => message.Text == "queued behind the first");

        connection.RaiseDisconnected();

        Assert.DoesNotContain(queued, vm.Messages);
        Assert.Contains("not sent", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disconnected", vm.StatusMessage!, StringComparison.OrdinalIgnoreCase);

        firstTurn.SetResult(true);
        await firstSend;
        Assert.Single(connection.Prompts);
    }

    [Fact]
    public async Task Cancel_DispatchesWhateverIsQueued_OnceTheStoppedTurnHasEnded()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = content => Text(content) == "first" ? firstTurn.Task : Task.CompletedTask };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "queued behind the first";
        await vm.SendAsync();
        var queued = Assert.Single(vm.Messages, message => message.Text == "queued behind the first");

        await vm.CancelCommand.ExecuteAsync(null);
        Assert.Single(connection.Prompts);
        Assert.True(queued.IsPending);

        firstTurn.SetResult(true);
        await firstSend;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);

        Assert.Equal(1, connection.CancelCount);
        Assert.Equal("queued behind the first", Text(connection.Prompts[1]));
        Assert.Contains(queued, vm.Messages);
        Assert.False(queued.IsPending);
        Assert.DoesNotContain("not sent", vm.StatusMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueueingAgent_FollowUpIsSentDuringAMultiOperationTurn_BeforeThatTurnCompletes()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendCommand.ExecuteAsync(null);
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "tool-1", Title = "Read a.cs", Status = ToolCallStatus.Completed }));
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "tool-2", Title = "Edit b.cs", Status = ToolCallStatus.InProgress }));

        vm.InputText = "follow-up";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["first", "follow-up"], connection.Prompts.Select(Text));
        Assert.False(firstSend.IsCompleted);
        Assert.Equal(0, connection.CancelCount);
        Assert.Equal(RecordingAcpAgentConnection.SessionId, Assert.Single(connection.PromptSessionIds.Distinct()));
        var followUp = Assert.Single(vm.Messages, message => message.Text == "follow-up");
        Assert.True(followUp.IsPending);

        turns.Complete("first");
        await firstSend;
        Assert.False(followUp.IsPending);
        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelCommand.CanExecute(null));

        turns.Complete("follow-up");
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.Equal(2, connection.Prompts.Count);
    }

    [Fact]
    public async Task QueueingAgent_SeveralFollowUps_AreHandedOverInTheOrderTheyWereSent()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        _ = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        vm.InputText = "third";
        await vm.SendAsync();

        Assert.Equal(["first", "second", "third"], connection.Prompts.Select(Text));

        turns.Complete("first");
        turns.Complete("second");
        turns.Complete("third");
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.Equal(3, connection.Prompts.Count);
        Assert.All(vm.Messages.Where(message => message.Role == ChatRole.User), message => Assert.False(message.IsPending));
    }

    [Fact]
    public async Task QueueingAgent_Cancel_SendsTheFollowUpsTheAgentWasHolding_OnceTheStopLands_InOrder()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        vm.InputText = "third";
        await vm.SendAsync();
        Assert.Equal(3, connection.Prompts.Count);

        await vm.CancelCommand.ExecuteAsync(null);
        turns.Complete("second", "cancelled");
        turns.Complete("third", "cancelled");
        vm.InputText = "draft";
        Assert.Equal("draft", vm.InputText);

        turns.Complete("first", "cancelled");
        await firstSend;
        await WaitUntilAsync(() => connection.Prompts.Count == 5);

        Assert.Equal(["first", "second", "third", "second", "third"], connection.Prompts.Select(Text));
        Assert.Equal("draft", vm.InputText);
        Assert.Contains(vm.Messages, message => message.Text == "second");
        Assert.Contains(vm.Messages, message => message.Text == "third");
        Assert.DoesNotContain("not sent", vm.StatusMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        turns.Complete("second");
        turns.Complete("third");
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.All(vm.Messages.Where(message => message.Role == ChatRole.User), message => Assert.False(message.IsPending));
    }

    [Fact]
    public async Task QueueingAgent_Disconnect_DropsFollowUpsTheAgentHadNotStarted_AndNeverResendsThem()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        var second = Assert.Single(vm.Messages, message => message.Text == "second");

        connection.RaiseDisconnected();

        Assert.DoesNotContain(second, vm.Messages);
        Assert.Contains("not sent", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        turns.Complete("second");
        turns.Complete("first");
        await firstSend;
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.Equal(["first", "second"], connection.Prompts.Select(Text));
    }

    [Fact]
    public async Task QueueingAgent_FailedFollowUp_ShowsTheError_AndIsSentAgainOnceTheTurnEnds()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "follow-up";
        await vm.SendAsync();
        var followUp = Assert.Single(vm.Messages, message => message.Text == "follow-up");

        turns.Fail("follow-up", new InvalidOperationException("prompt rejected"));
        Assert.Contains("prompt rejected", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(followUp.IsPending);
        Assert.True(vm.IsBusy);

        turns.Complete("first");
        await firstSend;
        await WaitUntilAsync(() => connection.Prompts.Count == 3);
        Assert.Equal(["first", "follow-up", "follow-up"], connection.Prompts.Select(Text));
        Assert.False(followUp.IsPending);
        turns.Complete("follow-up");
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public async Task QueueingAgent_CancelDuringAHandOff_DoesNotResendTheFollowUpTheAgentHadStarted()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "follow-up";
        await vm.SendAsync();
        var followUp = Assert.Single(vm.Messages, message => message.Text == "follow-up");

        await vm.CancelCommand.ExecuteAsync(null);
        turns.Complete("first");
        await firstSend;
        turns.Complete("follow-up", "cancelled");
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal(["first", "follow-up"], connection.Prompts.Select(Text));
        Assert.False(followUp.IsPending);
    }

    [Fact]
    public async Task QueueingAgent_CancelThatFails_SendsNothingTwice()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection
        {
            SupportsPromptQueueing = true,
            PromptHandler = turns.Handle,
            CancelHandler = () => Task.FromException(new InvalidOperationException("pipe broken")),
        };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "follow-up";
        await vm.SendAsync();

        await vm.CancelCommand.ExecuteAsync(null);
        Assert.Contains("Cancel failed", vm.StatusMessage, StringComparison.Ordinal);
        turns.Complete("first");
        await firstSend;
        turns.Complete("follow-up");
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal(["first", "follow-up"], connection.Prompts.Select(Text));
        Assert.All(vm.Messages.Where(message => message.Role == ChatRole.User), message => Assert.False(message.IsPending));
    }

    [Fact]
    public async Task QueueingAgent_MessageSentWhileStopping_IsSentAfterTheReturnedOne_InOrder()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();

        await vm.CancelCommand.ExecuteAsync(null);
        vm.InputText = "third";
        await vm.SendAsync();
        Assert.Equal(["first", "second"], connection.Prompts.Select(Text));

        turns.Complete("second", "cancelled");
        turns.Complete("first", "cancelled");
        await firstSend;
        await WaitUntilAsync(() => connection.Prompts.Count == 4);
        Assert.Equal(["first", "second", "second", "third"], connection.Prompts.Select(Text));
        Assert.Equal(string.Empty, vm.InputText);
        turns.Complete("second");
        turns.Complete("third");
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public async Task QueueingAgent_FailedTurn_LaterFollowUpGoesBackToTheComposerBehindTheReturnedOne()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        turns.Fail("second", new InvalidOperationException("agent error"));
        vm.InputText = "third";
        await vm.SendAsync();

        turns.Fail("first", new InvalidOperationException("agent error"));
        await firstSend;
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal(["first", "second"], connection.Prompts.Select(Text));
        Assert.Equal("second" + Environment.NewLine + Environment.NewLine + "third", vm.InputText);
    }

    [Fact]
    public async Task RunningAgentCount_DropsToZero_WhenTheTurnFails()
    {
        var turn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => turn.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "go";
        var prompt = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a1", Title = "Explore", IsSubagent = true, Status = ToolCallStatus.InProgress }));

        turn.SetException(new InvalidOperationException("agent error"));
        await prompt;

        Assert.Equal(0, vm.RunningAgentCount);
    }

    [Fact]
    public async Task QueueingAgent_HandOff_DoesNotSayClaudeFinished_WhileTheFollowUpIsStillRunning()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        var finished = new List<ChatAttentionEventArgs>();
        vm.AttentionRequested += (_, e) => { if (e.Kind == ChatAttentionKind.TurnCompleted) { finished.Add(e); } };
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("one"));
        vm.InputText = "follow-up";
        await vm.SendAsync();

        turns.Complete("first");
        await firstSend;
        Assert.Empty(finished);

        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("two"));
        turns.Complete("follow-up");
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.Equal("two", Assert.Single(finished).Message);
    }

    [Fact]
    public async Task QueueingAgent_Cancel_WithdrawnPromptsTurnEnd_DoesNotCloseTheStoppedTurnsBubble()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("partial"));
        vm.InputText = "second";
        await vm.SendAsync();
        var stopped = Assert.Single(vm.Messages, message => message.Role == ChatRole.Assistant);

        await vm.CancelCommand.ExecuteAsync(null);
        turns.Complete("second", "cancelled");
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk(" answer"));
        Assert.Null(stopped.DurationSeconds);
        Assert.Equal("partial answer", stopped.Text);

        turns.Complete("first", "cancelled");
        Assert.NotNull(stopped.DurationSeconds);
        await firstSend;
        await WaitUntilAsync(() => connection.Prompts.Count == 3);
        turns.Complete("second");
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public async Task QueueingAgent_HandOff_IsOneContinuingTurn_StatsCoverItFromTheStart()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "follow-up";
        await vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("one"));
        connection.RaiseSessionUpdate(new SessionUpdate.UsageUpdate(1_000, null, null, null));
        turns.Complete("first");
        await firstSend;

        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("two"));
        connection.RaiseSessionUpdate(new SessionUpdate.UsageUpdate(1_300, null, null, null));
        turns.Complete("follow-up");
        await WaitUntilAsync(() => !vm.IsBusy);

        var beforeHandOff = Assert.Single(vm.Messages, message => message.Text == "one");
        Assert.Null(beforeHandOff.DurationSeconds);
        Assert.Null(beforeHandOff.TokensUsed);
        var end = Assert.Single(vm.Messages, message => message.Text == "two");
        Assert.NotNull(end.DurationSeconds);
        Assert.Equal(1_300, end.TokensUsed);
    }

    [Fact]
    public async Task QueueingAgent_Cancel_StoppedTurnAnsweredFirst_FollowUpIsStillSentAgain()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        var second = Assert.Single(vm.Messages, message => message.Text == "second");

        await vm.CancelCommand.ExecuteAsync(null);
        turns.Complete("first", "cancelled");
        await firstSend;
        Assert.True(second.IsPending);
        turns.Complete("second", "cancelled");
        await WaitUntilAsync(() => connection.Prompts.Count == 3);

        Assert.Equal(["first", "second", "second"], connection.Prompts.Select(Text));
        Assert.Equal(string.Empty, vm.InputText);
        Assert.Contains(second, vm.Messages);
        Assert.False(second.IsPending);
        turns.Complete("second");
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public async Task QueueingAgent_MessageAfterARefusedFollowUp_KeepsItsPlaceBehindIt()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        turns.Fail("second", new InvalidOperationException("prompt rejected"));
        vm.InputText = "third";
        await vm.SendAsync();
        Assert.Equal(["first", "second"], connection.Prompts.Select(Text));

        turns.Complete("first");
        await firstSend;
        await WaitUntilAsync(() => connection.Prompts.Count == 4);
        Assert.Equal(["first", "second", "second", "third"], connection.Prompts.Select(Text));
        turns.Complete("second");
        turns.Complete("third");
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueueingAgent_AgentDies_FollowUpGoesBackToTheComposer_WhicheverPromptFailsFirst(bool runningFailsFirst)
    {
        var ui = new QueuedSynchronizationContext();
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = CreateOnUiContext(connection, ui);
        await vm.Initialization;
        ui.Drain();
        vm.InputText = "first";
        _ = vm.SendAsync();
        ui.Drain();
        vm.InputText = "second";
        _ = vm.SendAsync();
        ui.Drain();
        Assert.Equal(["first", "second"], connection.Prompts.Select(Text));
        var second = Assert.Single(vm.Messages, message => message.Text == "second");

        var died = new InvalidOperationException("agent exited");
        foreach (var text in runningFailsFirst ? new[] { "first", "second" } : new[] { "second", "first" })
        {
            turns.Fail(text, died);
        }

        connection.RaiseDisconnected();
        ui.Drain();
        ui.Drain();

        Assert.DoesNotContain(second, vm.Messages);
        Assert.Equal("second", vm.InputText);
        Assert.Equal(["first", "second"], connection.Prompts.Select(Text));
    }

    [Fact]
    public async Task NonQueueingAgent_FollowUpWaitsForTheRunningTurnToEnd()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "follow-up";
        await vm.SendAsync();

        Assert.Single(connection.Prompts);

        turns.Complete("first");
        await firstSend;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);
        turns.Complete("follow-up");
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public async Task QueuedMessage_StartFailsBeforeItReachesTheAgent_GoesBackToTheComposer()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => firstTurn.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        var second = Assert.Single(vm.Messages, message => message.Text == "second");
        bool armed = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (!armed || e.PropertyName != nameof(ChatViewModel.IsBusy) || !vm.IsBusy)
            {
                return;
            }

            armed = false;

            throw new InvalidOperationException("observer failed");
        };

        firstTurn.SetResult(true);
        await firstSend;
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Single(connection.Prompts);
        Assert.DoesNotContain(second, vm.Messages);
        Assert.Equal("second", vm.InputText);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueingAgent_RunningPromptFails_ThenAFollowUpEndsLast_RefusedFollowUpGoesBackToTheComposer()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        vm.InputText = "third";
        await vm.SendAsync();
        Assert.Equal(["first", "second", "third"], connection.Prompts.Select(Text));

        turns.Fail("second", new InvalidOperationException("prompt rejected"));
        turns.Fail("first", new InvalidOperationException("agent error"));
        await firstSend;
        turns.Complete("third");
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal(["first", "second", "third"], connection.Prompts.Select(Text));
        Assert.Equal("second", vm.InputText);
    }

    [Fact]
    public async Task QueueingAgent_Stop_RefusedFollowUpAnswersLast_IsStillSentAgain()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();

        await vm.CancelCommand.ExecuteAsync(null);
        turns.Complete("first", "cancelled");
        await firstSend;
        turns.Fail("second", new InvalidOperationException("prompt rejected"));
        await WaitUntilAsync(() => connection.Prompts.Count == 3);

        Assert.Equal(["first", "second", "second"], connection.Prompts.Select(Text));
        Assert.Equal(string.Empty, vm.InputText);
        turns.Complete("second");
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public async Task QueueingAgent_ObserverThrowsWhileAPromptReturns_PanelStillGoesIdle_AndShowsTheError()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        vm.InputText = "third";
        await vm.SendAsync();
        var third = Assert.Single(vm.Messages, message => message.Text == "third");
        third.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMessageViewModel.IsPending))
            {
                throw new InvalidOperationException("observer failed");
            }
        };

        turns.Complete("first");
        await firstSend;
        turns.Complete("second");
        turns.Complete("third");
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueingAgent_StopFails_MessageTypedMeanwhileIsSentAhead()
    {
        var turns = new PromptGate();
        var cancel = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle, CancelHandler = () => cancel.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        var stopping = vm.CancelCommand.ExecuteAsync(null);
        vm.InputText = "second";
        await vm.SendAsync();
        Assert.Single(connection.Prompts);

        cancel.SetException(new InvalidOperationException("pipe closed"));
        await stopping;

        Assert.Equal(["first", "second"], connection.Prompts.Select(Text));
        turns.Complete("first");
        await firstSend;
        turns.Complete("second");
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Theory]
    [InlineData("new chat")]
    [InlineData("open session")]
    public async Task RunningAgentCount_DropsToZero_WhenTheSessionIsReplaced(string replacement)
    {
        var connection = new RecordingAcpAgentConnection();
        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult(RecordingAcpAgentConnection.SessionId, []));
        using var vm = Create(connection);
        await vm.Initialization;
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a1", Title = "Explore", IsSubagent = true, Status = ToolCallStatus.InProgress }));
        Assert.Equal(1, vm.RunningAgentCount);

        connection.NewSessionHandler = _ => Task.FromResult(new NewSessionResult("session-2", []));
        if (replacement == "new chat")
        {
            await vm.NewSessionAsync();
        }
        else
        {
            await vm.OpenSessionAsync(new SessionSummary("session-2", "/workspace", "Older chat", null));
        }

        Assert.Equal(0, vm.RunningAgentCount);
    }

    private static string Text(IReadOnlyList<ContentBlock> content) => Assert.IsType<ContentBlock.Text>(Assert.Single(content)).Value;

    private sealed class PromptGate
    {
        private readonly Dictionary<string, Queue<TaskCompletionSource<string>>> _pending = [];

        public Task<string> Handle(IReadOnlyList<ContentBlock> content)
        {
            var text = content.OfType<ContentBlock.Text>().First().Value;
            if (!_pending.TryGetValue(text, out var queue))
            {
                _pending[text] = queue = new Queue<TaskCompletionSource<string>>();
            }

            var tcs = new TaskCompletionSource<string>();
            queue.Enqueue(tcs);

            return tcs.Task;
        }

        public void Complete(string text, string stopReason = "end_turn") => _pending[text].Dequeue().SetResult(stopReason);

        public void Fail(string text, Exception error) => _pending[text].Dequeue().SetException(error);
    }

    [Fact]
    public async Task QueuedMessage_WaitsForAnInFlightConfigChange_AndStillGoesOutWhenItCompletes()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var config = new TaskCompletionSource<IReadOnlyList<SessionConfigOption>>();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options(), PromptHandler = _ => firstTurn.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "queued behind the first";
        await vm.SendAsync();

        connection.ConfigHandler = (_, _, _) => config.Task;
        var configChange = vm.SelectModelAsync(vm.AvailableModels[1]);
        Assert.True(vm.IsConfigBusy);

        firstTurn.SetResult(true);
        await firstSend;
        Assert.Single(connection.Prompts);

        config.SetResult(Options("opus"));
        await configChange;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);

        Assert.Equal("queued behind the first", Assert.IsType<ContentBlock.Text>(Assert.Single(connection.Prompts[1])).Value);
    }

    [Fact]
    public async Task QueuedMessages_AreDispatchedInTheOrderTheyWereSent()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => firstTurn.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        vm.InputText = "third";
        await vm.SendAsync();

        Assert.Collection(vm.Messages.Where(message => message.Role == ChatRole.User),
            message => Assert.False(message.IsPending),
            message => Assert.True(message.IsPending),
            message => Assert.True(message.IsPending));

        firstTurn.SetResult(true);
        await firstSend;
        await WaitUntilAsync(() => connection.Prompts.Count == 3);

        Assert.Equal("second", Assert.IsType<ContentBlock.Text>(Assert.Single(connection.Prompts[1])).Value);
        Assert.Equal("third", Assert.IsType<ContentBlock.Text>(Assert.Single(connection.Prompts[2])).Value);
        Assert.All(vm.Messages.Where(message => message.Role == ChatRole.User), message => Assert.False(message.IsPending));
    }

    [Fact]
    public async Task Dispose_WhileAMessageIsQueued_DispatchesNothingAndFaultsNothing()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => firstTurn.Task };
        var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "queued behind the first";
        await vm.SendAsync();

        vm.Dispose();
        firstTurn.SetResult(true);
        await firstSend;

        Assert.Single(connection.Prompts);
    }

    [Fact]
    public async Task FailedTurn_KeepsItsErrorVisible_WhenTheNextQueuedMessageIsDispatched()
    {
        var firstTurn = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection
        {
            PromptHandler = content => content.Any(block => block is ContentBlock.Text text && text.Value == "first")
                ? firstTurn.Task
                : Task.CompletedTask,
        };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "queued behind the first";
        await vm.SendAsync();

        firstTurn.SetException(new InvalidOperationException("the agent gave up"));
        await firstSend;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);

        Assert.Contains("the agent gave up", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupCommands_UseLatestListOnlyForReturnedSession()
    {
        var ready = new TaskCompletionSource<NewSessionResult>();
        var connection = new RecordingAcpAgentConnection { NewSessionHandler = _ => ready.Task };
        using var vm = Create(connection);
        vm.InputText = "/";
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("old", "Old", null)]), "returned-session");
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("foreign", "Foreign", null)]), "foreign-session");
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Review", "scope")]), "returned-session");
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("foreign-latest", "Foreign", null)]), "foreign-session");
        Assert.Equal(_clientSlashCommandNames, vm.SlashSuggestions.Select(c => c.Name));

        ready.SetResult(new NewSessionResult("returned-session", []));
        await vm.Initialization;

        var command = vm.SlashSuggestions[0];
        Assert.Equal("review", command.Name);
        Assert.Equal("scope", command.InputHint);
        Assert.True(vm.AreSlashSuggestionsVisible);
        Assert.Equal(new[] { "review" }.Concat(_clientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("wrong-session", "Wrong", null)]));
        Assert.Equal(new[] { "review" }.Concat(_clientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
    }

    [Fact]
    public async Task StartupCommands_LatestEmptyListReplacesBufferedCommands()
    {
        var ready = new TaskCompletionSource<NewSessionResult>();
        var connection = new RecordingAcpAgentConnection { NewSessionHandler = _ => ready.Task };
        using var vm = Create(connection);
        vm.InputText = "/";
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("removed", "Removed", null)]));
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([]));
        ready.SetResult(new NewSessionResult(RecordingAcpAgentConnection.SessionId, []));
        await vm.Initialization;

        Assert.Equal(_clientSlashCommandNames, vm.SlashSuggestions.Select(c => c.Name));
        Assert.Equal("login", vm.SelectedSlashSuggestion?.Name);
        Assert.True(vm.AreSlashSuggestionsVisible);
        Assert.False(string.IsNullOrWhiteSpace(vm.CommandCatalogStatus));
    }

    [Fact]
    public async Task CommandsChanged_ReplacesCatalogIncludingEmpty_AndIgnoresForeignSession()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "/";
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([
            new AvailableCommand("compact", "Compact", null), new AvailableCommand("review", "Review", "scope")]));
        vm.SelectedSlashSuggestion = vm.SlashSuggestions[1];
        var removedSelection = vm.SelectedSlashSuggestion;
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("help", "Help", null)]));
        Assert.Equal(new[] { "help" }.Concat(_clientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
        Assert.NotSame(removedSelection, vm.SelectedSlashSuggestion);
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([]), "foreign-session");
        Assert.Equal(new[] { "help" }.Concat(_clientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([]));

        Assert.Equal(_clientSlashCommandNames, vm.SlashSuggestions.Select(c => c.Name));
        Assert.Equal("login", vm.SelectedSlashSuggestion?.Name);
        Assert.True(vm.AreSlashSuggestionsVisible);
        Assert.False(string.IsNullOrWhiteSpace(vm.CommandCatalogStatus));
    }

    [Fact]
    public async Task InitializeNotification_QueuedUntilAfterSessionCreation_DoesNotSeedCommandCatalog()
    {
        var ui = new QueuedSynchronizationContext();
        var connection = new RecordingAcpAgentConnection();
        connection.InitializeHandler = _ =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("too-early", "Too early", null)]));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            return Task.CompletedTask;
        };
        using var vm = CreateOnUiContext(connection, ui);
        await vm.Initialization;
        vm.InputText = "/";
        ui.Drain();

        Assert.Equal(_clientSlashCommandNames, vm.SlashSuggestions.Select(c => c.Name));
        Assert.True(vm.AreSlashSuggestionsVisible);
    }

    [Fact]
    public async Task CommandsQueuedBeforeDisconnect_CannotRestoreClearedCatalog()
    {
        var ui = new QueuedSynchronizationContext();
        var connection = new RecordingAcpAgentConnection();
        using var vm = CreateOnUiContext(connection, ui);
        await vm.Initialization;
        vm.InputText = "/";
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Review", null)]));
        ui.Drain();
        Assert.Equal(new[] { "review" }.Concat(_clientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
        await Task.Run(() => connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("queued", "Queued", null)])));
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            connection.RaiseDisconnected();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        ui.Drain();
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("after-disconnect", "Stale", null)]));
        ui.Drain();

        Assert.Equal(_clientSlashCommandNames, vm.SlashSuggestions.Select(c => c.Name));
        Assert.Equal("login", vm.SelectedSlashSuggestion?.Name);
        Assert.True(vm.AreSlashSuggestionsVisible);
        Assert.False(string.IsNullOrWhiteSpace(vm.CommandCatalogStatus));
    }

    [Fact]
    public async Task ReconnectedCatalog_IgnoresOldConnectionEvenWhenSessionIdsMatch()
    {
        var first = new RecordingAcpAgentConnection();
        var second = new RecordingAcpAgentConnection();
        var factory = new SingleConnectionFactory(first);
        using var vm = new ChatViewModel(new StubChatSessionServices(factory, new AlwaysSignedInAuthService()));
        await vm.Initialization;
        first.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("old", "Old", null)]));
        first.RaiseDisconnected();
        factory.ConnectHandler = _ => Task.FromResult<IAcpAgentConnection>(second);
        vm.InputText = "reconnect";
        await vm.SendAsync();
        vm.InputText = "/";
        second.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("current", "Current", null)]));
        first.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("stale", "Stale", null)]));

        Assert.Equal(new[] { "current" }.Concat(_clientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
    }

    [Theory]
    [InlineData("/", true, "compact", "Compare", "review", "login", "logout")]
    [InlineData("/cO", true, "compact", "Compare")]
    [InlineData("/view", true)]
    [InlineData(" /co", false)]
    [InlineData("explain /co", false)]
    [InlineData("/co argument", false)]
    [InlineData("/co\t", false)]
    [InlineData("/co\n", false)]
    public async Task SlashSuggestions_FilterOnlyLeadingSlashPrefixWithoutWhitespace(string input, bool visible, params string[] names)
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([
            new AvailableCommand("compact", "Compact", null),
            new AvailableCommand("Compare", "Compare", "files"),
            new AvailableCommand("review", "Review", null)]));

        vm.InputText = input;

        Assert.Equal(names, vm.SlashSuggestions.Select(command => command.Name));
        Assert.Equal(visible, vm.AreSlashSuggestionsVisible);
    }

    [Fact]
    public async Task SlashSuggestionAcceptance_InsertsOnlyCommandAndSpace_WithoutSubmittingDraft()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Review changes", "scope")]));
        vm.AddImageAttachment("keep.png", "image/png", "AQID");
        var image = Assert.Single(vm.Attachments);
        vm.InputText = "/rev";
        vm.SelectedSlashSuggestion = Assert.Single(vm.SlashSuggestions);

        vm.ApplySlashSuggestionCommand.Execute(vm.SelectedSlashSuggestion);

        Assert.Equal("/review ", vm.InputText);
        Assert.Same(image, Assert.Single(vm.Attachments));
        Assert.False(vm.AreSlashSuggestionsVisible);
        Assert.Empty(connection.Prompts);
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task DismissSlashSuggestions_SuppressesRefreshUntilInputActuallyChanges()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Review", null)]));
        vm.InputText = "/re";
        Assert.True(vm.AreSlashSuggestionsVisible);

        vm.DismissSlashSuggestions();
        vm.InputText = "/re";
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Updated description", null)]));
        Assert.False(vm.AreSlashSuggestionsVisible);
        vm.InputText = "/rev";

        Assert.True(vm.AreSlashSuggestionsVisible);
        Assert.Equal("review", Assert.Single(vm.SlashSuggestions).Name);
    }

    [Theory]
    [InlineData(ToolCallStatus.Completed)]
    [InlineData(ToolCallStatus.Failed)]
    public async Task PendingPrompt_SeparatesThoughtsFromAnswer_AndTracksActivityUntilTaskCompletes(ToolCallStatus finalToolStatus)
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "question";
        var prompt = vm.SendAsync();
        var working = vm.ActivityText;
        Assert.Contains("working", working, StringComparison.OrdinalIgnoreCase);

        connection.RaiseSessionUpdate(new SessionUpdate.AgentThoughtChunk("private thought before answer"));
        var thinking = vm.ActivityText;
        Assert.False(string.IsNullOrWhiteSpace(thinking));
        Assert.NotEqual(working, thinking);
        Assert.Equal(string.Empty, Assert.Single(vm.Messages, message => message.Role == ChatRole.Assistant).Text);
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("visible "));
        var responding = vm.ActivityText;
        Assert.NotEqual(thinking, responding);
        connection.RaiseSessionUpdate(new SessionUpdate.AgentThoughtChunk("private thought during answer"));
        Assert.Equal(thinking, vm.ActivityText);
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("answer"));
        Assert.Equal(responding, vm.ActivityText);
        Assert.Equal("visible answer", Assert.Single(vm.Messages, message => message.Role == ChatRole.Assistant).Text);

        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "tool-1", Title = "Read source.cs", Status = ToolCallStatus.InProgress }));
        var toolActivity = vm.ActivityText;
        Assert.False(string.IsNullOrWhiteSpace(toolActivity));
        Assert.NotEqual(responding, toolActivity);
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "tool-1", Status = finalToolStatus }));
        Assert.Equal(working, vm.ActivityText);
        connection.RaiseSessionUpdate(new SessionUpdate.TurnEnded("end_turn"));
        Assert.True(vm.IsBusy);
        Assert.False(vm.AttachActiveDocumentCommand.CanExecute(null));
        Assert.False(prompt.IsCompleted);
        completed.SetResult(true);
        await prompt;

        Assert.False(vm.IsBusy);
        Assert.Equal("visible answer", Assert.Single(vm.Messages, message => message.Role == ChatRole.Assistant).Text);
    }

    [Fact]
    public async Task RunningAgentCount_CountsSubagentCallsUntilTheyFinish()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "go";
        var prompt = vm.SendAsync();
        Assert.Equal(0, vm.RunningAgentCount);

        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a1", Title = "Explore", IsSubagent = true, Status = ToolCallStatus.InProgress }));
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a2", Title = "Review", IsSubagent = true, Status = ToolCallStatus.Pending }));
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "r1", Title = "Read", Status = ToolCallStatus.InProgress }));
        Assert.Equal(2, vm.RunningAgentCount);

        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a1", Status = ToolCallStatus.InProgress }));
        Assert.Equal(2, vm.RunningAgentCount);
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a1", Status = ToolCallStatus.Completed }));
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a2", Status = ToolCallStatus.Failed }));
        Assert.Equal(0, vm.RunningAgentCount);

        completed.SetResult(true);
        await prompt;
    }

    [Fact]
    public async Task RunningAgentCount_DropsToZero_WhenTheAgentDisconnects()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "go";
        var prompt = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a1", Title = "Explore", IsSubagent = true, Status = ToolCallStatus.InProgress }));
        Assert.Equal(1, vm.RunningAgentCount);

        connection.RaiseDisconnected();

        Assert.Equal(0, vm.RunningAgentCount);
        completed.SetResult(true);
        await prompt;
    }

    [Fact]
    public async Task RunningAgentCount_DropsToZero_WhenAStoppedTurnEnds_WithoutAFinalSubagentStatus()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "go";
        var prompt = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a1", Title = "Explore", IsSubagent = true, Status = ToolCallStatus.InProgress }));

        await vm.CancelCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.RunningAgentCount);
        completed.SetResult(true);
        await prompt;

        Assert.Equal(0, vm.RunningAgentCount);
    }

    [Fact]
    public async Task RunningAgentCount_DropsWhenTheFinalStatusArrivesInALaterBubble()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "go";
        var prompt = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a1", Title = "Explore", IsSubagent = true, Status = ToolCallStatus.InProgress }));
        connection.RaiseSessionUpdate(new SessionUpdate.TurnEnded("end_turn"));

        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a1", Status = ToolCallStatus.Completed }));

        Assert.Equal(0, vm.RunningAgentCount);
        completed.SetResult(true);
        await prompt;
    }

    [Theory]
    [InlineData(0, "0 agents")]
    [InlineData(1, "1 agent")]
    [InlineData(3, "3 agents")]
    public async Task RunningAgentsLabel_ReadsNaturally(int running, string expected)
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "go";
        var prompt = vm.SendAsync();
        for (int i = 0; i < running; i++)
        {
            connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "a" + i, Title = "Agent", IsSubagent = true, Status = ToolCallStatus.InProgress }));
        }

        Assert.Equal(expected, vm.RunningAgentsLabel);
        completed.SetResult(true);
        await prompt;
    }

    [Fact]
    public async Task QueueingAgent_RunningPromptFails_WithoutADisconnect_FollowUpGoesBackToTheComposer()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        var second = Assert.Single(vm.Messages, message => message.Text == "second");

        turns.Fail("second", new InvalidOperationException("agent error"));
        turns.Fail("first", new InvalidOperationException("agent error"));
        await firstSend;
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.DoesNotContain(second, vm.Messages);
        Assert.Equal("second", vm.InputText);
        Assert.Equal(2, connection.Prompts.Count);
        Assert.Contains("agent error", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("message box", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueingAgent_FailedTurn_PutsTheFollowUpsAttachmentsBackInTheComposer_BesideTheDrafts()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        var screenshot = new ChatAttachmentViewModel("screenshot.png", "image/png", "AAAA");
        vm.Attachments.Add(screenshot);
        vm.InputText = "second";
        await vm.SendAsync();
        Assert.Empty(vm.Attachments);
        var drafted = new ChatAttachmentViewModel("drafted.png", "image/png", "BBBB");
        vm.Attachments.Add(drafted);

        turns.Fail("second", new InvalidOperationException("agent error"));
        turns.Fail("first", new InvalidOperationException("agent error"));
        await firstSend;
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal("second", vm.InputText);
        Assert.Equal(2, vm.Attachments.Count);
        Assert.Single(vm.Attachments, attachment => ReferenceEquals(attachment, screenshot));
        Assert.Single(vm.Attachments, attachment => ReferenceEquals(attachment, drafted));
    }

    [Fact]
    public async Task QueueingAgent_FollowUpReturnsBeforeTheRunningPrompt_StaysBusyUntilBothHave()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();

        turns.Complete("second");
        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelCommand.CanExecute(null));

        turns.Complete("first");
        await firstSend;
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.Equal(2, connection.Prompts.Count);
    }

    [Fact]
    public async Task QueueingAgent_PlanReview_GoesOutOnce_AfterTheLastPromptWithTheAgentReturns()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "plan the feature";
        var sending = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");

        turns.Complete("plan the feature");
        await sending;
        Assert.Equal(2, connection.Prompts.Count);

        turns.Complete("second");
        await WaitUntilAsync(() => connection.Prompts.Count == 3);
        Assert.Contains("Add a rollback step", Text(connection.Prompts[2]), StringComparison.Ordinal);
        await Task.Yield();
        Assert.Equal(3, connection.Prompts.Count);
    }

    [Fact]
    public async Task PlanReview_SentAsTheTurnEnds_ShowsAboveAMessageQueuedDuringIt()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "plan the feature";
        var sending = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");

        turns.Complete("plan the feature");
        await sending;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);

        Assert.Contains("Add a rollback step", Text(connection.Prompts[1]), StringComparison.Ordinal);
        var users = vm.Messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray();
        Assert.Equal(3, users.Length);
        Assert.Equal("plan the feature", users[0]);
        Assert.Contains("Add a rollback step", users[1], StringComparison.Ordinal);
        Assert.Equal("second", users[2]);
    }

    [Fact]
    public async Task PlanReview_SentAsTheTurnFails_KeepsThatTurnsErrorReadable()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "plan the feature";
        var sending = vm.SendAsync();
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");

        turns.Fail("plan the feature", new InvalidOperationException("agent crashed"));
        await sending;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);

        Assert.Contains("Add a rollback step", Text(connection.Prompts[1]), StringComparison.Ordinal);
        Assert.Contains("agent crashed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanReview_LeavesTheDraftsAttachmentsWithTheDraft()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "meanwhile, look at this";
        vm.AddImageAttachment("draft.png", "image/png", "AQID");
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);

        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");
        await WaitUntilAsync(() => connection.Prompts.Count == 1);

        Assert.IsType<ContentBlock.Text>(Assert.Single(connection.Prompts[0]));
        Assert.Equal("meanwhile, look at this", vm.InputText);
        Assert.Equal("AQID", Assert.Single(vm.Attachments).Base64Data);
    }

    [Fact]
    public async Task Send_ByTheUser_ClearsTheErrorThePreviousTurnLeft()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var first = vm.SendAsync();
        turns.Fail("first", new InvalidOperationException("agent crashed"));
        await first;
        Assert.Contains("agent crashed", vm.StatusMessage, StringComparison.Ordinal);

        vm.InputText = "second";
        var second = vm.SendAsync();
        turns.Complete("second");
        await second;

        Assert.True(string.IsNullOrEmpty(vm.StatusMessage), vm.StatusMessage);
    }

    [Fact]
    public async Task Send_TextTypedWhileTheTurnRuns_GetsNoNoticeWhenTheTurnEndsNormally()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var sending = vm.SendAsync();
        vm.InputText = "typed meanwhile";

        turns.Complete("first");
        await sending;

        Assert.True(string.IsNullOrEmpty(vm.StatusMessage), vm.StatusMessage);
        Assert.Equal("typed meanwhile", vm.InputText);
    }

    [Fact]
    public async Task SendWhileBusy_ObserverThrowsAsTheComposerEmpties_ReportsIt_AndStillSendsTheMessage()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var sending = vm.SendAsync();
        vm.InputText = "second";
        bool thrown = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.InputText) && vm.InputText.Length == 0 && !thrown)
            {
                thrown = true;

                throw new InvalidOperationException("observer failed");
            }
        };

        var failure = await Record.ExceptionAsync(() => vm.SendAsync());

        Assert.Null(failure);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(vm.Messages, message => message.Role == ChatRole.User && message.Text == "second" && message.IsPending);
        turns.Complete("first");
        await sending;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);
        Assert.Equal("second", Text(connection.Prompts[1]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disposed_ObserverThrowsAsTheTurnEnds_DropsACancellation_LogsAnythingElse(bool cancellation)
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService());
        var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "first";
        var sending = vm.SendAsync();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsBusy) && !vm.IsBusy)
            {
                throw cancellation ? new OperationCanceledException("observer cancelled") : new InvalidOperationException("observer failed");
            }
        };

        vm.Dispose();
        turns.Complete("first");
        await sending;

        if (cancellation)
        {
            Assert.Empty(services.LoggedErrors);
        }
        else
        {
            Assert.Equal("observer failed", Assert.Single(services.LoggedErrors).Exception.Message);
        }

        Assert.True(string.IsNullOrEmpty(vm.StatusMessage), vm.StatusMessage);
    }

    [Fact]
    public async Task PlanReview_ObserverThrowsAsTheReviewEntersTheComposer_KeepsTheReviewAndTheDraft()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "meanwhile, look at this";
        vm.AddImageAttachment("draft.png", "image/png", "AQID");
        bool thrown = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.InputText) && vm.InputText.StartsWith("Review comments", StringComparison.Ordinal) && !thrown)
            {
                thrown = true;

                throw new InvalidOperationException("observer failed");
            }
        };
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);

        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");

        Assert.Empty(connection.Prompts);
        Assert.Equal("Review comments on the plan:\nAdd a rollback step." + Environment.NewLine + Environment.NewLine + "meanwhile, look at this", vm.InputText);
        Assert.Equal("AQID", Assert.Single(vm.Attachments).Base64Data);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendWhileBusy_ObserverThrowsAsTheBubbleIsAdded_TheMessageLeavesTheComposer_AndGoesOutOnce()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var sending = vm.SendAsync();
        vm.InputText = "second";
        vm.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add &&
                e.NewItems!.Cast<ChatMessageViewModel>().Any(message => message.Text == "second"))
            {
                throw new InvalidOperationException("observer failed");
            }
        };

        await vm.SendAsync();

        Assert.Equal(string.Empty, vm.InputText);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
        turns.Complete("first");
        await sending;
        await WaitUntilAsync(() => connection.Prompts.Count == 2);
        Assert.Equal(new[] { "first", "second" }, connection.Prompts.Select(Text));
    }

    [Fact]
    public async Task PlanReview_ObserverThrowsAsTheDraftIsSetAside_LosesNeitherTheDraftNorTheReview()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "meanwhile, look at this";
        vm.AddImageAttachment("draft.png", "image/png", "AQID");
        vm.Attachments.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                throw new InvalidOperationException("observer failed");
            }
        };
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);

        var thrown = Record.Exception(() => vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step."));

        Assert.Null(thrown);
        Assert.Empty(connection.Prompts);
        Assert.Equal("Review comments on the plan:\nAdd a rollback step." + Environment.NewLine + Environment.NewLine + "meanwhile, look at this", vm.InputText);
        Assert.Equal("AQID", Assert.Single(vm.Attachments).Base64Data);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanReview_SentAsTheTurnEnds_ObserverThrowsAsTheDraftComesBack_IsReported_AndTheQueueStillGoesOut()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "plan the feature";
        var sending = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");
        vm.InputText = "half-written idea";
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.InputText) && vm.InputText.Contains("half-written", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("observer failed");
            }
        };

        turns.Complete("plan the feature");
        var failure = await Record.ExceptionAsync(() => sending);
        await WaitUntilAsync(() => connection.Prompts.Count == 2);
        turns.Complete(Text(connection.Prompts[1]));
        await WaitUntilAsync(() => connection.Prompts.Count == 3);

        Assert.Null(failure);
        Assert.Contains("Add a rollback step", Text(connection.Prompts[1]), StringComparison.Ordinal);
        Assert.Equal("second", Text(connection.Prompts[2]));
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanReview_ObserverThrowsAsTheDraftsImageComesBack_IsReported_NotThrownAtTheCommand()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "meanwhile, look at this";
        vm.AddImageAttachment("draft.png", "image/png", "AQID");
        vm.Attachments.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                throw new InvalidOperationException("observer failed");
            }
        };
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);

        var thrown = Record.Exception(() => vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step."));
        await WaitUntilAsync(() => connection.Prompts.Count == 1);

        Assert.Null(thrown);
        Assert.IsType<ContentBlock.Text>(Assert.Single(connection.Prompts[0]));
        Assert.Equal("meanwhile, look at this", vm.InputText);
        Assert.Equal("AQID", Assert.Single(vm.Attachments).Base64Data);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanReview_ObserverThrowsAsTheDraftsTextComesBack_TheDraftsImageComesBackToo()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "meanwhile, look at this";
        vm.AddImageAttachment("draft.png", "image/png", "AQID");
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.InputText) && vm.InputText == "meanwhile, look at this")
            {
                throw new InvalidOperationException("observer failed");
            }
        };
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);

        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");
        await WaitUntilAsync(() => connection.Prompts.Count == 1);

        Assert.Equal("meanwhile, look at this", vm.InputText);
        Assert.Equal("AQID", Assert.Single(vm.Attachments).Base64Data);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentDisconnects_WhileATurnRuns_TheDroppedQueueNoticeStays_AndTheTeardownIsNotLogged()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { PromptHandler = turns.Handle };
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService());
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "first";
        var sending = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();

        connection.RaiseDisconnected();
        Assert.Contains("A queued message was not sent.", vm.StatusMessage, StringComparison.Ordinal);
        turns.Fail("first", new System.IO.IOException("The JSON-RPC connection was closed."));
        await sending;

        Assert.Contains("A queued message was not sent.", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(services.LoggedErrors);
    }

    [Fact]
    public async Task PlanReview_FailingAtOnce_KeepsBothTheReviewAndTheDraft()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "meanwhile, what about the CI job?";
        vm.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add &&
                e.NewItems!.Cast<ChatMessageViewModel>().Any(message => message.Text.StartsWith("Review comments", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("observer failed");
            }
        };
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);

        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");

        Assert.False(vm.IsBusy);
        Assert.Empty(connection.Prompts);
        Assert.Equal("Review comments on the plan:\nAdd a rollback step." + Environment.NewLine + Environment.NewLine + "meanwhile, what about the CI job?", vm.InputText);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanReview_OnTheUiThread_SendsTheReview_AndPutsTheDraftBack()
    {
        var ui = new QueuedSynchronizationContext();
        var connection = new RecordingAcpAgentConnection();
        using var vm = CreateOnUiContext(connection, ui);
        await vm.Initialization;
        ui.Drain();
        vm.InputText = "meanwhile, what about the CI job?";
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        ui.Drain();

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try { vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step."); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        ui.Drain();

        Assert.Equal("Review comments on the plan:\nAdd a rollback step.", Text(Assert.Single(connection.Prompts)));
        Assert.Equal("meanwhile, what about the CI job?", vm.InputText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task QueueingAgent_DisposeWithAFollowUpHeldByTheAgent_SendsNothingMore()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();

        vm.Dispose();
        turns.Fail("second", new InvalidOperationException("agent gone"));
        turns.Complete("first", "cancelled");
        await firstSend;

        Assert.Equal(["first", "second"], connection.Prompts.Select(Text));
        Assert.Equal(0, connection.CancelCount);
    }

    [Fact]
    public async Task QueueingAgent_ThoughtAfterAHandOff_LandsInANewReplyBelowTheFollowUp()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("working on it"));
        vm.InputText = "second";
        await vm.SendAsync();
        var firstReply = Assert.Single(vm.Messages, message => message.Role == ChatRole.Assistant);

        turns.Complete("first");
        await firstSend;
        connection.RaiseSessionUpdate(new SessionUpdate.AgentThoughtChunk("about the second message"));

        var followUp = Assert.Single(vm.Messages, message => message.Text == "second");
        var reply = vm.Messages[^1];
        Assert.Equal(ChatRole.Assistant, reply.Role);
        Assert.NotSame(firstReply, reply);
        Assert.True(vm.Messages.IndexOf(reply) > vm.Messages.IndexOf(followUp));
        Assert.IsType<ChatThinkingPart>(Assert.Single(reply.Parts));
        Assert.DoesNotContain(firstReply.Parts, part => part is ChatThinkingPart);

        turns.Complete("second");
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public async Task QueueingAgent_WorkspaceSwitch_DropsHeldFollowUps_AndLeavesThePanelUsable()
    {
        var turns = new PromptGate();
        var connection = new RecordingAcpAgentConnection { SupportsPromptQueueing = true, PromptHandler = turns.Handle };
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService(), workspaceRoot: @"C:\ProjectA");
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "first";
        var firstSend = vm.SendAsync();
        vm.InputText = "second";
        await vm.SendAsync();

        services.SetWorkspaceRoot(@"C:\ProjectB");
        turns.Complete("second", "cancelled");
        turns.Complete("first", "cancelled");
        await firstSend;
        await WaitUntilAsync(() => !vm.IsBusy && vm.SessionTitle == "Untitled");

        Assert.DoesNotContain(vm.Messages, message => message.Text == "second");
        Assert.NotEqual("second", vm.InputText);
        Assert.Equal(2, connection.Prompts.Count);

        vm.InputText = "in project B";
        var next = vm.SendAsync();
        Assert.True(vm.IsBusy);
        Assert.Equal(3, connection.Prompts.Count);
        turns.Complete("in project B");
        await next;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ThoughtChunks_AreShownInTheTranscript_InOrder_ButNotInTheReplyText()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "go";
        var prompt = vm.SendAsync();

        connection.RaiseSessionUpdate(new SessionUpdate.AgentThoughtChunk("The branch is "));
        connection.RaiseSessionUpdate(new SessionUpdate.AgentThoughtChunk("fix/38."));
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "read", Title = "Read a.cs", Status = ToolCallStatus.Completed }));
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("done"));

        var reply = Assert.Single(vm.Messages, message => message.Role == ChatRole.Assistant);
        Assert.Collection(reply.Parts,
            part => Assert.Equal("The branch is fix/38.", Assert.IsType<ChatThinkingPart>(part).Text),
            part => Assert.IsType<ChatToolCallPart>(part),
            part => Assert.Equal("done", Assert.IsType<ChatTextPart>(part).Text));
        Assert.Equal("done", reply.Text);
        completed.SetResult(true);
        await prompt;
    }

    [Fact]
    public async Task TurnEnded_SetsDurationSecondsOnTheAssistantMessage_ButNotBeforeTheTurnEnds()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "question";
        var prompt = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("answer"));

        var assistantMessage = Assert.Single(vm.Messages, message => message.Role == ChatRole.Assistant);
        Assert.Null(assistantMessage.DurationSeconds);

        connection.RaiseSessionUpdate(new SessionUpdate.TurnEnded("end_turn"));

        Assert.NotNull(assistantMessage.DurationSeconds);

        completed.SetResult(true);
        await prompt;
    }

    [Fact]
    public async Task PendingPermission_OverridesThoughtResponseAndToolActivityUntilChoice()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "edit file";
        var prompt = vm.SendAsync();
        var working = vm.ActivityText;
        var call = new ToolCallUpdate { ToolCallId = "permission-tool", Title = "Edit source.cs", Status = ToolCallStatus.Pending };
        var request = connection.RaisePermissionRequested(call, [new PermissionOption { OptionId = "allow-once", Label = "Allow", Outcome = PermissionOutcome.AllowOnce }]);
        var permissionActivity = vm.ActivityText;
        Assert.NotEqual(working, permissionActivity);
        Assert.False(string.IsNullOrWhiteSpace(permissionActivity));

        connection.RaiseSessionUpdate(new SessionUpdate.AgentThoughtChunk("not answer text"));
        Assert.Equal(permissionActivity, vm.ActivityText);
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("Waiting for approval."));
        Assert.Equal(permissionActivity, vm.ActivityText);
        connection.RaiseSessionUpdate(new SessionUpdate.ToolCall(new ToolCallUpdate { ToolCallId = "other-tool", Title = "Read another.cs", Status = ToolCallStatus.InProgress }));
        Assert.Equal(permissionActivity, vm.ActivityText);
        Assert.True(vm.IsBusy);
        var permission = vm.PendingPermission!;
        permission.ChooseCommand.Execute(permission.Options[0]);
        Assert.Equal("allow-once", await request.Response.Task);
        Assert.Null(vm.PendingPermission);
        Assert.NotEqual(permissionActivity, vm.ActivityText);
        Assert.True(vm.IsBusy);
        completed.SetResult(true);
        await prompt;

        Assert.Equal("Waiting for approval.", Assert.Single(vm.Messages, message => message.Role == ChatRole.Assistant).Text);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CommandCatalogStatus_DistinguishesDiscoveryNoMatchEmptyAndDisconnected()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "/";
        var discovery = vm.CommandCatalogStatus;
        Assert.False(string.IsNullOrWhiteSpace(discovery));
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Review", null)]));
        Assert.True(string.IsNullOrEmpty(vm.CommandCatalogStatus));
        vm.InputText = "/missing";
        var noMatch = vm.CommandCatalogStatus;
        Assert.False(string.IsNullOrWhiteSpace(noMatch));
        Assert.NotEqual(discovery, noMatch);
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([]));
        var empty = vm.CommandCatalogStatus;
        Assert.False(string.IsNullOrWhiteSpace(empty));
        Assert.NotEqual(noMatch, empty);
        connection.RaiseDisconnected();

        Assert.False(string.IsNullOrWhiteSpace(vm.CommandCatalogStatus));
        Assert.NotEqual(empty, vm.CommandCatalogStatus);
        Assert.Empty(vm.SlashSuggestions);
    }

    [Fact]
    public async Task UserMessageChunk_EchoedDuringALiveTurn_DoesNotDuplicateTheUsersBubble()
    {
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { PromptHandler = _ => completed.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "summarize this file";
        var prompt = vm.SendAsync();

        connection.RaiseSessionUpdate(new SessionUpdate.UserMessageChunk("summarize this file"));

        var user = Assert.Single(vm.Messages, message => message.Role == ChatRole.User);
        Assert.Equal("summarize this file", user.Text);
        completed.SetResult(true);
        await prompt;
    }

    [Fact]
    public async Task NewChat_AdoptsACommandCatalogPublishedBeforeTheNewSessionIdIsKnown()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        var ready = new TaskCompletionSource<NewSessionResult>();
        connection.NewSessionHandler = _ => ready.Task;

        var switching = vm.NewSessionAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("review", "Review", "scope")]), "session-2");
        ready.SetResult(new NewSessionResult("session-2", []));
        await switching;

        vm.InputText = "/";
        Assert.Equal(new[] { "review" }.Concat(_clientSlashCommandNames), vm.SlashSuggestions.Select(c => c.Name));
        Assert.Empty(vm.CommandCatalogStatus);
    }

    private static (ToolCallUpdate Call, List<PermissionOption> Options) PlanApprovalRequest() =>
        (new ToolCallUpdate { ToolCallId = "plan-1", Title = "Approve Plan", Kind = "switch_mode", Status = ToolCallStatus.Pending, Content = [new ToolCallContent { Text = "# Plan" }] },
        [
            new PermissionOption { OptionId = "allow-once", Label = "Yes, proceed", Outcome = PermissionOutcome.AllowOnce },
            new PermissionOption { OptionId = "reject-once", Label = "No, keep planning", Outcome = PermissionOutcome.RejectOnce },
        ]);

    [Fact]
    public async Task PlanReview_HeldBackByAConfigChangeInFlight_GoesOutOnceTheChangeCompletes()
    {
        var turn = new TaskCompletionSource<bool>();
        var config = new TaskCompletionSource<IReadOnlyList<SessionConfigOption>>();
        var connection = new RecordingAcpAgentConnection
        {
            ConfigOptions = Options(),
            PromptHandler = _ => turn.Task,
            ConfigHandler = (_, _, _) => config.Task,
        };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "plan the feature";
        var sending = vm.SendAsync();
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");
        var changing = vm.SelectModelAsync(vm.AvailableModels[1]);
        Assert.True(vm.IsConfigBusy);

        turn.SetResult(true);
        await sending;
        Assert.Single(connection.Prompts);

        config.SetResult(Options("opus"));
        await changing;

        await WaitUntilAsync(() => connection.Prompts.Count == 2);
        Assert.Contains("Add a rollback step", Assert.IsType<ContentBlock.Text>(connection.Prompts[^1][0]).Value, StringComparison.Ordinal);
        Assert.Equal("opus", vm.SelectedModel!.Value);
    }

    [Fact]
    public async Task PlanReview_WhileADocumentCaptureIsInFlight_WaitsForItAndKeepsTheDraft()
    {
        var capture = new TaskCompletionSource<EditorDocumentSnapshot?>();
        var connection = new RecordingAcpAgentConnection();
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            CaptureHandler = _ => capture.Task,
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        vm.InputText = "meanwhile, what about the CI job?";
        var attaching = vm.AttachActiveDocumentCommand.ExecuteAsync(null);
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);

        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");

        Assert.Equal("meanwhile, what about the CI job?", vm.InputText);
        Assert.Empty(connection.Prompts);

        capture.SetResult(null);
        await attaching;

        await WaitUntilAsync(() => connection.Prompts.Count == 1);
        Assert.Contains("Add a rollback step", Assert.IsType<ContentBlock.Text>(connection.Prompts[0][0]).Value, StringComparison.Ordinal);
        await WaitUntilAsync(() => vm.InputText.Length > 0);
        Assert.Equal("meanwhile, what about the CI job?", vm.InputText);
    }

    private static ChatViewModel CreateOnUiContext(RecordingAcpAgentConnection connection, SynchronizationContext ui)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            return Create(connection);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
