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
    private static IReadOnlyList<SessionConfigOption> Options(string model = "sonnet", string effort = "medium", params string[] efforts) =>
    [
        new SessionConfigOption("model-choice", "Model", "model", model,
        [new SessionConfigValue("sonnet", "Sonnet", "Fast"), new SessionConfigValue("opus", "Opus", "Capable")]),
        new SessionConfigOption("reasoning", "Effort", "thought_level", effort,
            (efforts.Length == 0 ? new[] { "low", "medium" } : efforts)
            .Select(value => new SessionConfigValue(value, value, null)).ToArray()),
    ];

    private static IReadOnlyList<SessionConfigOption> OptionsWithMode(string mode = "default") =>
    [
        .. Options(),
        new SessionConfigOption("mode", "Mode", "mode", mode,
        [
            new SessionConfigValue("default", "Manual", "Always ask before making changes"),
            new SessionConfigValue("acceptEdits", "Accept edits", "Automatically accept all file edits"),
            new SessionConfigValue("plan", "Plan", "Create a plan before making changes"),
        ]),
    ];

    private static ChatViewModel Create(RecordingAcpAgentConnection connection) =>
        new(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService()));

    [Fact]
    public async Task Startup_PreparesAdvertisedCurrentSettingsBeforeFirstPrompt()
    {
        var ready = new TaskCompletionSource<NewSessionResult>();
        var connection = new RecordingAcpAgentConnection { NewSessionHandler = _ => ready.Task };
        using var vm = Create(connection);
        vm.InputText = "first prompt";
        Assert.True(vm.IsConnecting);
        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.Null(vm.SelectedModel);

        ready.SetResult(new NewSessionResult(RecordingAcpAgentConnection.SessionId, Options()));
        await vm.Initialization;

        Assert.Equal("sonnet", vm.SelectedModel!.Value);
        Assert.Equal("medium", vm.SelectedEffort!.Value);
        Assert.True(vm.CanConfigure);
        Assert.Empty(connection.Prompts);
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnknownCurrentModel_DoesNotPretendFirstAdvertisedModelIsActive()
    {
        using var vm = Create(new RecordingAcpAgentConnection { ConfigOptions = Options("unadvertised") });
        await vm.Initialization;
        Assert.Null(vm.SelectedModel);
        Assert.NotEqual(vm.AvailableModels[0].Name, vm.ActiveModelName);
    }

    [Fact]
    public async Task FailedSettingChange_RetainsAcknowledgedSelectionAndDraft()
    {
        var connection = new RecordingAcpAgentConnection
        {
            ConfigOptions = Options(),
            ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("Setting rejected")),
        };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "draft";

        await vm.SelectModelAsync(vm.AvailableModels[1]);

        Assert.Equal("sonnet", vm.SelectedModel!.Value);
        Assert.Equal("medium", vm.SelectedEffort!.Value);
        Assert.Equal("draft", vm.InputText);
        Assert.NotNull(vm.StatusMessage);
        Assert.True(vm.CanConfigure);
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task PendingSettings_BlockPromptAndSecondChange_ThenApplyAuthoritativeEffortCatalog()
    {
        var response = new TaskCompletionSource<IReadOnlyList<SessionConfigOption>>();
        var connection = new RecordingAcpAgentConnection
        {
            ConfigOptions = Options(),
            ConfigHandler = (_, _, _) => response.Task,
        };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "use acknowledged settings";
        var changing = vm.SelectModelAsync(vm.AvailableModels[1]);

        Assert.True(vm.IsConfigBusy);
        Assert.False(vm.CanConfigure);
        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.Equal("sonnet", vm.SelectedModel!.Value);
        await vm.SelectEffortAsync(vm.AvailableEfforts[0]);
        await vm.SendAsync();
        Assert.Empty(connection.Prompts);
        Assert.Single(connection.ConfigChanges);

        response.SetResult(Options("opus", "maximum", "balanced", "maximum"));
        await changing;
        Assert.Equal("opus", vm.SelectedModel!.Value);
        Assert.Equal(new[] { "balanced", "maximum" }, vm.AvailableEfforts.Select(value => value.Value));
        Assert.Equal("maximum", vm.SelectedEffort!.Value);
        await vm.SendAsync();
        Assert.Single(connection.Prompts);
    }

    [Fact]
    public async Task ConfigResponse_NotRequestedValue_IsDisplayedAsAuthoritative()
    {
        var connection = new RecordingAcpAgentConnection
        {
            ConfigOptions = Options(),
            ConfigHandler = (_, _, _) => Task.FromResult(Options("sonnet", "low")),
        };
        using var vm = Create(connection);
        await vm.Initialization;
        await vm.SelectModelAsync(vm.AvailableModels[1]);
        Assert.Equal("sonnet", vm.SelectedModel!.Value);
        Assert.Equal("low", vm.SelectedEffort!.Value);

        connection.RaiseSessionUpdate(new SessionUpdate.ConfigOptionsChanged(Options("opus", "high", "high")));
        Assert.Equal("opus", vm.SelectedModel.Value);
        Assert.Equal("high", vm.SelectedEffort.Value);
    }

    [Fact]
    public async Task Mode_PopulatesAvailableModesAndSelectedMode_FromInitialConfigOptions()
    {
        using var vm = Create(new RecordingAcpAgentConnection { ConfigOptions = OptionsWithMode("acceptEdits") });
        await vm.Initialization;

        Assert.True(vm.HasModes);
        Assert.Equal(new[] { "default", "acceptEdits", "plan" }, vm.AvailableModes.Select(value => value.Value));
        Assert.Equal("acceptEdits", vm.SelectedMode!.Value);
        Assert.Equal("Accept edits", vm.ActiveModeName);
    }

    [Fact]
    public async Task NoModeOption_HasModesFalse_AndAvailableModesEmpty()
    {
        using var vm = Create(new RecordingAcpAgentConnection { ConfigOptions = Options() });
        await vm.Initialization;

        Assert.False(vm.HasModes);
        Assert.Empty(vm.AvailableModes);
        Assert.Null(vm.SelectedMode);
    }

    // The agent's response is authoritative for mode exactly as it is for model/effort: the stub
    // deliberately answers with a mode that was not the one clicked.
    [Fact]
    public async Task SelectModeAsync_SendsConfigChange_AndUpdatesSelectionFromResponse()
    {
        var connection = new RecordingAcpAgentConnection
        {
            ConfigOptions = OptionsWithMode(),
            ConfigHandler = (_, _, _) => Task.FromResult(OptionsWithMode("acceptEdits")),
        };
        using var vm = Create(connection);
        await vm.Initialization;
        Assert.Equal("default", vm.SelectedMode!.Value);

        await vm.SelectModeAsync(vm.AvailableModes[2]);

        Assert.Equal(("mode", "plan"), connection.ConfigChanges.Single());
        Assert.Equal("acceptEdits", vm.SelectedMode!.Value);
    }

    [Fact]
    public async Task SessionFailure_DisposesAcquiredConnectionAndRemovesItsSubscriptions()
    {
        var connection = new RecordingAcpAgentConnection
        {
            NewSessionHandler = _ => Task.FromException<NewSessionResult>(new InvalidOperationException("Session rejected")),
        };
        using var vm = Create(connection);
        await vm.Initialization;
        Assert.Equal(1, connection.DisposeCount);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.CanConfigure);
        Assert.Empty(vm.AvailableModels);
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("stale event"));
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task DisposeDuringConnectionAcquisition_DisposesLateConnectionWithoutPublishingSession()
    {
        var acquired = new TaskCompletionSource<IAcpAgentConnection>();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var factory = new SingleConnectionFactory(connection) { ConnectHandler = _ => acquired.Task };
        var vm = new ChatViewModel(new StubChatSessionServices(factory, new AlwaysSignedInAuthService()));
        vm.Dispose();
        acquired.SetResult(connection);
        await vm.Initialization;
        Assert.Equal(1, connection.DisposeCount);
        Assert.False(connection.IsInitialized);
        Assert.Empty(vm.AvailableModels);
        Assert.False(vm.CanConfigure);
    }

    [Fact]
    public async Task DisposeDuringSessionCreation_DoesNotRepublishLateSession()
    {
        var ready = new TaskCompletionSource<NewSessionResult>();
        var connection = new RecordingAcpAgentConnection { NewSessionHandler = _ => ready.Task };
        var vm = Create(connection);
        vm.Dispose();
        ready.SetResult(new NewSessionResult(RecordingAcpAgentConnection.SessionId, Options()));
        await vm.Initialization;
        Assert.Equal(1, connection.DisposeCount);
        Assert.Empty(vm.AvailableModels);
        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReconnectFailure_PreservesDraftAndImages_WithoutAddingUnsentTranscript()
    {
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var factory = new SingleConnectionFactory(connection);
        using var vm = new ChatViewModel(new StubChatSessionServices(factory, new AlwaysSignedInAuthService()));
        await vm.Initialization;
        connection.RaiseDisconnected();
        factory.ConnectHandler = _ => Task.FromException<IAcpAgentConnection>(new InvalidOperationException("Unavailable"));
        vm.InputText = "keep me";
        vm.AddImageAttachment("draft.png", "image/png", "AQID");

        await vm.SendAsync();

        Assert.Equal("keep me", vm.InputText);
        Assert.Equal("AQID", Assert.Single(vm.Attachments).Base64Data);
        Assert.Empty(vm.Messages);
        Assert.Empty(vm.AvailableModels);
        Assert.False(vm.CanConfigure);
    }

    [Fact]
    public async Task DisconnectedSession_ReconnectsAndUsesNewAuthoritativeSettings()
    {
        var first = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        var second = new RecordingAcpAgentConnection { ConfigOptions = Options("opus") };
        var factory = new SingleConnectionFactory(first);
        using var vm = new ChatViewModel(new StubChatSessionServices(factory, new AlwaysSignedInAuthService()));
        await vm.Initialization;
        first.RaiseDisconnected();
        factory.ConnectHandler = _ => Task.FromResult<IAcpAgentConnection>(second);
        vm.InputText = "retry connection";
        await vm.SendAsync();
        Assert.Empty(first.Prompts);
        Assert.Single(second.Prompts);
        Assert.Equal("opus", vm.SelectedModel!.Value);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task ImageOnlyDraft_CanBeRemovedOrSentWithExactMediaPayload()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = Create(connection);
        await vm.Initialization;
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.AddImageAttachment("remove.png", "image/png", "BAUG");
        var removed = Assert.Single(vm.Attachments);
        vm.RemoveAttachmentCommand.Execute(removed);
        Assert.False(vm.SendCommand.CanExecute(null));

        vm.InputText = "  ";
        vm.AddImageAttachment("capture.png", "image/png", "AQID");
        Assert.True(vm.SendCommand.CanExecute(null));
        await vm.SendAsync();

        var image = Assert.IsType<ContentBlock.Image>(Assert.Single(Assert.Single(connection.Prompts)));
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("AQID", image.Base64Data);
        Assert.Empty(vm.Attachments);
        Assert.Equal("capture.png", Assert.Single(Assert.Single(vm.Messages).Images).Name);
        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task PromptFailureAfterSubmission_DoesNotRestoreOrDuplicateAcceptedDraft()
    {
        var connection = new RecordingAcpAgentConnection
        {
            PromptHandler = _ => Task.FromException(new InvalidOperationException("Connection lost after submission")),
        };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "submitted once";
        vm.AddImageAttachment("capture.png", "image/png", "AQID");
        await vm.SendAsync();
        await vm.SendAsync();
        Assert.Single(connection.Prompts);
        Assert.Collection(connection.Prompts[0],
            block => Assert.Equal("submitted once", Assert.IsType<ContentBlock.Text>(block).Value),
            block => Assert.Equal("AQID", Assert.IsType<ContentBlock.Image>(block).Base64Data));
        Assert.Single(vm.Messages);
        Assert.Empty(vm.InputText);
        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public async Task IsBusy_StillPermitsChangingSessionSettings_ViaItsOwnConcurrentAcpRequest()
    {
        // Model/mode/effort changes are their own ACP RPC call over the same JSON-RPC connection as
        // an in-flight prompt, which already supports concurrent in-flight requests. Other clients
        // (the reference VS Code extension, the CLI) let you switch settings mid-turn, so this one
        // must not force you to interrupt/cancel first just to do the same thing.
        var completion = new TaskCompletionSource<bool>();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options(), PromptHandler = _ => completion.Task };
        using var vm = Create(connection);
        await vm.Initialization;
        vm.InputText = "pending turn";
        var prompt = vm.SendAsync();
        connection.RaiseSessionUpdate(new SessionUpdate.TurnEnded("end_turn"));

        Assert.True(vm.IsBusy);
        Assert.True(vm.CanConfigure);
        await vm.SelectModelAsync(vm.AvailableModels[1]);
        Assert.Single(connection.ConfigChanges);

        completion.SetResult(true);
        await prompt;
        Assert.True(vm.CanConfigure);
    }

    [Fact]
    public async Task UnknownNativeAuth_AttemptsSessionWithoutClaimingSignInUntilItSucceeds()
    {
        var ready = new TaskCompletionSource<NewSessionResult>();
        var connection = new RecordingAcpAgentConnection { NewSessionHandler = _ => ready.Task };
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AdvisoryAuthService(AuthState.Unknown)));
        Assert.False(vm.IsSignedIn);
        Assert.False(vm.NeedsAuthentication);
        Assert.True(vm.IsConnecting);
        ready.SetResult(new NewSessionResult(RecordingAcpAgentConnection.SessionId, Options()));
        await vm.Initialization;
        Assert.True(vm.IsSignedIn);
        Assert.True(vm.CanConfigure);
    }

    [Fact]
    public async Task DefinitivelySignedOut_DoesNotAcquireConnection()
    {
        var connection = new RecordingAcpAgentConnection();
        using var vm = new ChatViewModel(new StubChatSessionServices(new SingleConnectionFactory(connection), new AdvisoryAuthService(AuthState.SignedOut)));
        await vm.Initialization;
        vm.InputText = "needs login";
        await vm.SendAsync();
        Assert.True(vm.NeedsAuthentication);
        Assert.False(connection.IsInitialized);
        Assert.Empty(connection.Prompts);
    }

    [Fact]
    public async Task BackgroundConfigurationUpdate_ChangesCollectionsOnlyOnCapturedUiContext()
    {
        var ui = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options() };
        ChatViewModel vm;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            vm = Create(connection);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var collectionContexts = new List<SynchronizationContext?>();
        vm.AvailableEfforts.CollectionChanged += (_, _) => collectionContexts.Add(SynchronizationContext.Current);
        await Task.Run(() => connection.RaiseSessionUpdate(
            new SessionUpdate.ConfigOptionsChanged(Options("opus", "high", "high"))));
        Assert.Equal("sonnet", vm.SelectedModel!.Value);
        Assert.Empty(collectionContexts);
        ui.Drain();
        Assert.Equal("opus", vm.SelectedModel!.Value);
        Assert.Equal("high", Assert.Single(vm.AvailableEfforts).Value);
        Assert.All(collectionContexts, context => Assert.Same(ui, context));
        vm.Dispose();
        ui.Drain();
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue(() => callback(state));

        public void Drain()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_callbacks.TryDequeue(out var callback)) callback();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class AdvisoryAuthService(AuthState state) : IAcpAuthService
    {
        public AuthState CurrentState => state;
        public event EventHandler<AuthStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) => Task.CompletedTask;
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
