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
            services.CaptureHandler = _ => Task.FromException<EditorDocumentSnapshot?>(new InvalidOperationException("Editor unavailable"));
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
    [InlineData("busy")]
    [InlineData("disposed")]
    public async Task DraftGates_BlockCaptureImageMutationAndSend(string gate)
    {
        var ready = new TaskCompletionSource<NewSessionResult>();
        var config = new TaskCompletionSource<IReadOnlyList<SessionConfigOption>>();
        var completed = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        if (gate == "connecting") connection.NewSessionHandler = _ => ready.Task;
        if (gate == "configuring") connection.ConfigHandler = (_, _, _) => config.Task;
        if (gate == "busy") connection.PromptHandler = _ => completed.Task;
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
        if (gate != "connecting") await vm.Initialization;
        if (gate is "configuring" or "busy" or "disposed")
        {
            vm.AddImageAttachment("retained.png", "image/png", "AQID");
            if (gate == "configuring") pending = vm.SelectModelAsync(vm.AvailableModels[1]);
            if (gate == "busy") pending = vm.SendAsync();
            if (gate == "disposed") vm.Dispose();
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
        completed.TrySetResult(true);
        await pending;
        await vm.Initialization;
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
        Assert.Empty(vm.SlashSuggestions);

        ready.SetResult(new NewSessionResult("returned-session", []));
        await vm.Initialization;

        var command = Assert.Single(vm.SlashSuggestions);
        Assert.Equal("review", command.Name);
        Assert.Equal("scope", command.InputHint);
        Assert.True(vm.AreSlashSuggestionsVisible);
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([new AvailableCommand("wrong-session", "Wrong", null)]));
        Assert.Equal("review", Assert.Single(vm.SlashSuggestions).Name);
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

        Assert.Empty(vm.SlashSuggestions);
        Assert.Null(vm.SelectedSlashSuggestion);
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
        Assert.Equal("help", Assert.Single(vm.SlashSuggestions).Name);
        Assert.NotSame(removedSelection, vm.SelectedSlashSuggestion);
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([]), "foreign-session");
        Assert.Equal("help", Assert.Single(vm.SlashSuggestions).Name);
        connection.RaiseSessionUpdate(new SessionUpdate.AvailableCommandsChanged([]));

        Assert.Empty(vm.SlashSuggestions);
        Assert.Null(vm.SelectedSlashSuggestion);
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

        Assert.Empty(vm.SlashSuggestions);
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
        Assert.Equal("review", Assert.Single(vm.SlashSuggestions).Name);
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

        Assert.Empty(vm.SlashSuggestions);
        Assert.Null(vm.SelectedSlashSuggestion);
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

        Assert.Equal("current", Assert.Single(vm.SlashSuggestions).Name);
    }

    [Theory]
    [InlineData("/", true, "compact", "Compare", "review")]
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
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.Assistant);
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

    // SendCoreAsync already added the user's bubble; an agent that echoes the prompt back as
    // user_message_chunk during the live turn must not produce a second one. Replay (a loaded
    // session, IsBusy false) is the case that legitimately builds the bubble.
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

    // New Chat issues the same session/new as the initial connect, so it needs the same buffering:
    // the agent publishes the new session's catalog before the response resolves, while _sessionId
    // still names the previous session.
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
        Assert.Equal("review", Assert.Single(vm.SlashSuggestions).Name);
        Assert.Empty(vm.CommandCatalogStatus);
    }

    private static (ToolCallUpdate Call, List<PermissionOption> Options) PlanApprovalRequest() =>
        (new ToolCallUpdate { ToolCallId = "plan-1", Title = "Approve Plan", Kind = "switch_mode", Status = ToolCallStatus.Pending, Content = [new ToolCallContent { Text = "# Plan" }] },
        [
            new PermissionOption { OptionId = "allow-once", Label = "Yes, proceed", Outcome = PermissionOutcome.AllowOnce },
            new PermissionOption { OptionId = "reject-once", Label = "No, keep planning", Outcome = PermissionOutcome.RejectOnce },
        ]);

    // Review comments go out when the composer frees up, whichever blocker was holding it. A
    // model/mode change (allowed mid-turn) still in flight when the rejected plan's turn returns
    // must not strand them until the user's next unrelated Send.
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
        Assert.Single(connection.Prompts); // the composer is still blocked by the config change

        config.SetResult(Options("opus"));
        await changing;

        await WaitUntilAsync(() => connection.Prompts.Count == 2);
        Assert.Contains("Add a rollback step", Assert.IsType<ContentBlock.Text>(connection.Prompts[^1][0]).Value, StringComparison.Ordinal);
        Assert.Equal("opus", vm.SelectedModel!.Value);
    }

    // With a document capture in flight the composer cannot send, so the review has to wait for
    // the capture rather than be typed over the user's draft and left there unsent.
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
        // No local turn owns IsBusy (a plan can arrive from a remote-driven turn), so the review is
        // due as soon as the composer can take it.
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
