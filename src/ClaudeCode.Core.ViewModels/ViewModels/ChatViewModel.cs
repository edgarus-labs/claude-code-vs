using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels;

public sealed class ChatViewModel : ObservableObject, IDisposable
{
    private readonly IChatSessionServices _services;
    private readonly SynchronizationContext? _uiContext;
    private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private IAcpAgentConnection? _connection;
    private string? _sessionId;
    private SessionConfigOption? _modelOption;
    private SessionConfigOption? _effortOption;
    private SessionConfigValue? _selectedModel;
    private SessionConfigValue? _selectedEffort;
    private ChatMessageViewModel? _currentAssistantMessage;
    private ChatMessageViewModel? _currentUserMessage;
    private bool _isHistoryOpen;
    private bool _isHistoryLoading;
    private string? _historyError;
    private IReadOnlyList<AvailableCommand> _availableCommands = Array.Empty<AvailableCommand>();
    private Dictionary<string, IReadOnlyList<AvailableCommand>>? _pendingCommandCatalogs;
    private AvailableCommand? _selectedSlashSuggestion;
    private bool _hasCommandCatalog;
    private bool _slashSuggestionsDismissed;
    private bool _areSlashSuggestionsVisible;
    private bool _isCapturingDocument;
    private string _commandCatalogStatus = "Connect to Claude to discover commands.";
    private string _activityText = string.Empty;
    private string? _attachmentError;
    private bool _disposed;
    private bool _isBusy;
    private bool _isConnecting;
    private bool _isConfigBusy;
    private bool _isSignedIn;
    private bool _needsAuthentication;
    private string _inputText = string.Empty;
    private string? _statusMessage;
    private PlanViewModel? _currentPlan;
    private PermissionRequestViewModel? _pendingPermission;
    private TaskCompletionSourceSlot<string>? _pendingPermissionResponse;
    private ElicitationRequestViewModel? _pendingElicitation;
    private TaskCompletionSourceSlot<ElicitationAnswer>? _pendingElicitationResponse;

    private const long MaxImageAttachmentBytes = 5L * 1024 * 1024;
    private const long MaxDocumentAttachmentBytes = 1L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _emptyElicitationContent =
        new Dictionary<string, IReadOnlyList<string>>();

    public ChatViewModel(IChatSessionServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ChatViewModel must be constructed on a thread with a SynchronizationContext (e.g. the WPF UI thread).");
        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsBusy && !_disposed && _connection is not null && _sessionId is not null);
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => !IsSignedIn && !_disposed);
        NewSessionCommand = new AsyncRelayCommand(NewSessionAsync, () => CanEditDraft);
        ShowHistoryCommand = new AsyncRelayCommand(ShowHistoryAsync, () => CanEditDraft);
        OpenSessionCommand = new AsyncRelayCommand<SessionSummary>(OpenSessionAsync, session => CanEditDraft && session is not null);
        AttachActiveDocumentCommand = new AsyncRelayCommand(AttachActiveDocumentAsync, () => CanEditDraft && !_isCapturingDocument);
        ApplySlashSuggestionCommand = new RelayCommand<AvailableCommand>(ApplySlashSuggestion,
            command => CanEditDraft && AreSlashSuggestionsVisible && command is not null && SlashSuggestions.Contains(command));
        RemoveAttachmentCommand = new RelayCommand<ChatAttachmentViewModel>(attachment =>
        {
            if (attachment is not null && CanEditDraft)
            {
                Attachments.Remove(attachment);
            }
        }, _ => CanEditDraft);
        Attachments.CollectionChanged += (_, __) => SendCommand.NotifyCanExecuteChanged();
        _services.AuthService.StateChanged += OnAuthStateChanged;
        ApplyAuthState(_services.AuthService.CurrentState);
        Initialization = InitializeAsync();
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new ObservableCollection<ChatMessageViewModel>();
    public ObservableCollection<ChatAttachmentViewModel> Attachments { get; } = new ObservableCollection<ChatAttachmentViewModel>();
    public ObservableCollection<SessionConfigValue> AvailableModels { get; } = new ObservableCollection<SessionConfigValue>();
    public ObservableCollection<SessionConfigValue> AvailableEfforts { get; } = new ObservableCollection<SessionConfigValue>();
    public ObservableCollection<AvailableCommand> SlashSuggestions { get; } = new ObservableCollection<AvailableCommand>();
    public ObservableCollection<SessionSummary> SessionHistory { get; } = new ObservableCollection<SessionSummary>();
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand SignInCommand { get; }
    public IAsyncRelayCommand NewSessionCommand { get; }
    public IAsyncRelayCommand ShowHistoryCommand { get; }
    public IAsyncRelayCommand<SessionSummary> OpenSessionCommand { get; }
    public IRelayCommand<ChatAttachmentViewModel> RemoveAttachmentCommand { get; }
    public IAsyncRelayCommand AttachActiveDocumentCommand { get; }
    public IRelayCommand<AvailableCommand> ApplySlashSuggestionCommand { get; }
    public Task Initialization { get; }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                _slashSuggestionsDismissed = false;
                RefreshSlashSuggestions();
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) NotifyStateChanged(); }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set { if (SetProperty(ref _isConnecting, value)) NotifyStateChanged(); }
    }

    public bool IsConfigBusy
    {
        get => _isConfigBusy;
        private set { if (SetProperty(ref _isConfigBusy, value)) NotifyStateChanged(); }
    }

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set { if (SetProperty(ref _isSignedIn, value)) NotifyStateChanged(); }
    }

    public bool NeedsAuthentication
    {
        get => _needsAuthentication;
        private set { if (SetProperty(ref _needsAuthentication, value)) NotifyStateChanged(); }
    }

    private bool CanEditDraft => !_disposed && !NeedsAuthentication && !IsConnecting && !IsBusy && !IsConfigBusy;
    public bool CanConfigure => CanEditDraft && !_isCapturingDocument && _sessionId is not null;
    public bool HasEffort => AvailableEfforts.Count > 0;
    public string ActiveModelName => _selectedModel?.Name ?? "Model unavailable";
    public string ActiveEffortName => _selectedEffort?.Name ?? string.Empty;

    public SessionConfigValue? SelectedModel
    {
        get => _selectedModel;
        set => _ = SelectModelAsync(value);
    }

    public SessionConfigValue? SelectedEffort
    {
        get => _selectedEffort;
        set => _ = SelectEffortAsync(value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? AttachmentError
    {
        get => _attachmentError;
        set => SetProperty(ref _attachmentError, value);
    }

    public bool IsHistoryOpen
    {
        get => _isHistoryOpen;
        private set => SetProperty(ref _isHistoryOpen, value);
    }

    public bool IsHistoryLoading
    {
        get => _isHistoryLoading;
        private set => SetProperty(ref _isHistoryLoading, value);
    }

    public string? HistoryError
    {
        get => _historyError;
        private set => SetProperty(ref _historyError, value);
    }

    public void CloseHistory() => RunOnUi(() => IsHistoryOpen = false);

    public string ActivityText
    {
        get => _activityText;
        private set => SetProperty(ref _activityText, value);
    }

    public AvailableCommand? SelectedSlashSuggestion
    {
        get => _selectedSlashSuggestion;
        set => SetProperty(ref _selectedSlashSuggestion, value);
    }

    public bool AreSlashSuggestionsVisible
    {
        get => _areSlashSuggestionsVisible;
        private set => SetProperty(ref _areSlashSuggestionsVisible, value);
    }

    public string CommandCatalogStatus
    {
        get => _commandCatalogStatus;
        private set => SetProperty(ref _commandCatalogStatus, value);
    }

    public PlanViewModel? CurrentPlan
    {
        get => _currentPlan;
        private set => SetProperty(ref _currentPlan, value);
    }

    public PermissionRequestViewModel? PendingPermission
    {
        get => _pendingPermission;
        private set => SetProperty(ref _pendingPermission, value);
    }

    public ElicitationRequestViewModel? PendingElicitation
    {
        get => _pendingElicitation;
        private set { if (SetProperty(ref _pendingElicitation, value)) OnPropertyChanged(nameof(IsElicitationOpen)); }
    }

    public bool IsElicitationOpen => _pendingElicitation is not null;

    public void AddImageAttachment(string name, string mimeType, string base64Data)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An image name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(mimeType) || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An image media type is required.", nameof(mimeType));
        if (string.IsNullOrWhiteSpace(base64Data)) throw new ArgumentException("Image data is required.", nameof(base64Data));
        RunOnUi(() =>
        {
            if (!CanEditDraft) return;
            if (EstimateBase64ByteLength(base64Data) > MaxImageAttachmentBytes)
            {
                AttachmentError = "Image exceeds the 5 MB attachment limit.";
                return;
            }
            Attachments.Add(new ChatAttachmentViewModel(name, mimeType, base64Data));
            AttachmentError = null;
        });
    }

    private static long EstimateBase64ByteLength(string base64Data) => (long)base64Data.Length * 3 / 4;

    private Task AttachActiveDocumentAsync(CancellationToken cancellationToken) =>
        OnUiAsync(() => AttachActiveDocumentCoreAsync(cancellationToken));

    private async Task AttachActiveDocumentCoreAsync(CancellationToken cancellationToken)
    {
        if (!CanEditDraft || _isCapturingDocument) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _isCapturingDocument = true;
        NotifyStateChanged();
        AttachmentError = null;
        try
        {
            var document = await _services.CaptureActiveDocumentAsync(linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested();
            if (!CanEditDraft) return;
            if (document is null)
            {
                AttachmentError = "Open a text document in the editor before attaching it.";
                return;
            }
            if (Encoding.UTF8.GetByteCount(document.Text) > MaxDocumentAttachmentBytes)
            {
                AttachmentError = "Document exceeds the 1 MB attachment limit.";
                return;
            }

            var attachment = new ChatAttachmentViewModel(document);
            for (var index = 0; index < Attachments.Count; index++)
            {
                if (string.Equals(Attachments[index].DocumentPath, attachment.DocumentPath, StringComparison.OrdinalIgnoreCase))
                {
                    Attachments[index] = attachment;
                    return;
                }
            }
            Attachments.Add(attachment);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed) AttachmentError = $"Could not attach the active document: {ex.Message}";
        }
        finally
        {
            _isCapturingDocument = false;
            NotifyStateChanged();
        }
    }

    public void DismissSlashSuggestions() => RunOnUi(() =>
    {
        _slashSuggestionsDismissed = true;
        UpdateSlashPresentation();
    });

    private void ApplySlashSuggestion(AvailableCommand? command)
    {
        if (!CanEditDraft || !AreSlashSuggestionsVisible || command is null || !SlashSuggestions.Contains(command)) return;
        InputText = "/" + command.Name + " ";
    }

    private void ApplyCommandCatalog(IReadOnlyList<AvailableCommand> commands)
    {
        _availableCommands = commands;
        _hasCommandCatalog = true;
        RefreshSlashSuggestions();
    }

    private bool IsSlashToken => InputText.StartsWith("/", StringComparison.Ordinal) && !InputText.Any(char.IsWhiteSpace);

    private void RefreshSlashSuggestions()
    {
        var isSlashToken = IsSlashToken;
        var selectedName = SelectedSlashSuggestion?.Name;
        SlashSuggestions.Clear();
        if (isSlashToken)
        {
            var prefix = InputText.Substring(1);
            foreach (var command in _availableCommands)
                if (command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) SlashSuggestions.Add(command);
        }
        SelectedSlashSuggestion = SlashSuggestions.FirstOrDefault(command => command.Name == selectedName)
            ?? SlashSuggestions.FirstOrDefault();
        UpdateSlashPresentation();
    }

    private void UpdateSlashPresentation()
    {
        var isSlashToken = IsSlashToken;
        AreSlashSuggestionsVisible = CanEditDraft && isSlashToken && !_slashSuggestionsDismissed;
        CommandCatalogStatus = _sessionId is null
            ? (IsConnecting ? "Connecting to discover commands…" : "Connect to Claude to discover commands.")
            : !_hasCommandCatalog ? "Discovering commands from Claude…"
            : _availableCommands.Count == 0 ? "Claude has not advertised any commands for this session."
            : isSlashToken && SlashSuggestions.Count == 0 ? "No commands match this name."
            : string.Empty;
        ApplySlashSuggestionCommand.NotifyCanExecuteChanged();
    }

    private void UpdateActivity(string activity)
    {
        if (IsBusy) ActivityText = PendingPermission is null ? activity : "Waiting for permission…";
    }

    public Task SelectModelAsync(SessionConfigValue? value) => OnUiAsync(() => ChangeConfigAsync(_modelOption, value));
    public Task SelectEffortAsync(SessionConfigValue? value) => OnUiAsync(() => ChangeConfigAsync(_effortOption, value));

    private async Task ChangeConfigAsync(SessionConfigOption? option, SessionConfigValue? value)
    {
        if (!CanConfigure || option is null || value is null || value.Value == option.CurrentValue ||
            !option.Options.Any(candidate => candidate.Value == value.Value))
        {
            NotifySelectionsChanged();
            return;
        }

        var connection = _connection!;
        var sessionId = _sessionId!;
        IsConfigBusy = true;
        StatusMessage = null;
        try
        {
            var options = await connection.SetSessionConfigOptionAsync(sessionId, option.Id, value.Value, _lifetime.Token).ConfigureAwait(true);
            if (!_disposed && ReferenceEquals(connection, _connection) && sessionId == _sessionId)
                ApplyConfigOptions(options);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not change session settings: {ex.Message}";
        }
        finally
        {
            // Never publish an optimistic selection: failures retain the last acknowledged state.
            IsConfigBusy = false;
            NotifySelectionsChanged();
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => OnUiAsync(() => InitializeCoreAsync(cancellationToken));

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        IsConnecting = true;
        try
        {
            var signedIn = await _services.AuthService.IsSignedInAsync(linked.Token).ConfigureAwait(true);
            if (_disposed) return;
            IsSignedIn = signedIn || (_sessionId is not null && _services.AuthService.CurrentState != AuthState.SignedOut);
            NeedsAuthentication = !signedIn && _services.AuthService.CurrentState == AuthState.SignedOut;
            if (!NeedsAuthentication) await EnsureConnectedAsync(linked.Token).ConfigureAwait(true);
            else await ReleaseConnectionAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not prepare Claude: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public Task SignInAsync() => OnUiAsync(SignInCoreAsync);

    private async Task SignInCoreAsync()
    {
        if (_disposed) return;
        StatusMessage = "Signing in to Claude...";
        var progress = new Progress<string>(message => RunOnUi(() => { if (!_disposed) StatusMessage = message; }));
        try
        {
            await _services.AuthService.SignInAsync(_lifetime.Token, progress).ConfigureAwait(true);
            await InitializeCoreAsync(_lifetime.Token).ConfigureAwait(true);
            if (!_disposed && !IsSignedIn) StatusMessage = "Sign-in did not complete.";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Sign-in failed: {ex.Message}";
        }
    }

    private bool CanSend() => CanEditDraft && !_isCapturingDocument &&
        (!string.IsNullOrWhiteSpace(InputText) || Attachments.Count > 0);

    public Task SendAsync() => OnUiAsync(SendCoreAsync);

    private async Task SendCoreAsync()
    {
        if (!CanSend()) return;
        ActivityText = "Working…";
        IsBusy = true;
        StatusMessage = null;
        try
        {
            // Acquire before consuming the draft: failed startup must not lose text or attachments.
            var (connection, sessionId) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed) return;
            var text = InputText.Trim();
            var attachments = Attachments.ToArray();
            var content = new List<ContentBlock>(attachments.Length + 1);
            if (text.Length > 0) content.Add(new ContentBlock.Text(text));
            foreach (var attachment in attachments)
                content.Add(attachment.ToContentBlock());

            var transcriptText = string.Join(Environment.NewLine, new[] { text }
                .Where(part => part.Length > 0).Concat(attachments.Select(attachment =>
                    (attachment.IsImage ? "[Image: " : "[Document: ") + attachment.Name + "]")));
            Messages.Add(new ChatMessageViewModel(ChatRole.User, transcriptText));
            InputText = string.Empty;
            Attachments.Clear();
            AttachmentError = null;
            _currentAssistantMessage = null;
            // Once submitted, acceptance is ambiguous on transport failure. Do not restore/resend it.
            await connection.SendPromptAsync(sessionId, content, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ActivityText = string.Empty;
            _currentAssistantMessage = null;
        }
    }

    public Task CancelAsync() => OnUiAsync(CancelCoreAsync);

    private async Task CancelCoreAsync()
    {
        if (_disposed || _connection is null || _sessionId is null) return;
        try
        {
            await _connection.CancelAsync(_sessionId, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Cancel failed: {ex.Message}";
        }
    }

    public Task NewSessionAsync() => OnUiAsync(NewSessionCoreAsync);

    private async Task NewSessionCoreAsync()
    {
        if (!CanEditDraft) return;
        try
        {
            var (connection, _) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            var cwd = _services.WorkspaceRoot ?? Environment.CurrentDirectory;
            var session = await connection.NewSessionAsync(cwd, null, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            ResetTranscriptState();
            _sessionId = session.SessionId;
            ApplyConfigOptions(session.ConfigOptions);
            StatusMessage = null;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not start a new session: {ex.Message}";
        }
        finally
        {
            NotifyStateChanged();
        }
    }

    public Task ShowHistoryAsync() => OnUiAsync(ShowHistoryCoreAsync);

    private async Task ShowHistoryCoreAsync()
    {
        if (!CanEditDraft) return;
        IsHistoryOpen = true;
        IsHistoryLoading = true;
        HistoryError = null;
        try
        {
            var (connection, _) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            var cwd = _services.WorkspaceRoot ?? Environment.CurrentDirectory;
            var sessions = await connection.ListSessionsAsync(cwd, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            SessionHistory.Clear();
            foreach (var session in sessions) SessionHistory.Add(session);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) HistoryError = $"Could not load session history: {ex.Message}";
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    public Task OpenSessionAsync(SessionSummary? session) => OnUiAsync(() => OpenSessionCoreAsync(session));

    private async Task OpenSessionCoreAsync(SessionSummary? session)
    {
        if (session is null || !CanEditDraft) return;
        IsHistoryOpen = false;
        StatusMessage = null;
        try
        {
            var (connection, _) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            ResetTranscriptState();
            // Known upfront (unlike session/new): set it before the call below so replayed
            // session/update notifications, tagged with this id, are not dropped by OnSessionUpdate's
            // "belongs to the known session" check while the request is still in flight.
            _sessionId = session.SessionId;
            var result = await connection.LoadSessionAsync(session.SessionId, session.Cwd, null, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            ApplyConfigOptions(result.ConfigOptions);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not open session: {ex.Message}";
        }
        finally
        {
            NotifyStateChanged();
        }
    }

    private void ResetTranscriptState()
    {
        Messages.Clear();
        _currentAssistantMessage = null;
        _currentUserMessage = null;
        CurrentPlan = null;
        _pendingPermissionResponse?.TrySetException(new OperationCanceledException("The session was replaced."));
        _pendingPermissionResponse = null;
        PendingPermission = null;
        _pendingElicitationResponse?.TrySetResult(new ElicitationAnswer(ElicitationAction.Cancel, _emptyElicitationContent));
        _pendingElicitationResponse = null;
        PendingElicitation = null;
        _availableCommands = Array.Empty<AvailableCommand>();
        _hasCommandCatalog = false;
        RefreshSlashSuggestions();
        ActivityText = string.Empty;
    }

    private async Task<(IAcpAgentConnection connection, string sessionId)> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_connection is not null && _sessionId is not null) return (_connection, _sessionId);
            IsConnecting = true;
            var connection = await _services.ConnectionFactory.ConnectAsync(cancellationToken).ConfigureAwait(true);
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                await connection.DisposeAsync().ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(ChatViewModel));
            }

            _connection = connection;
            connection.SessionUpdate += OnSessionUpdate;
            connection.PermissionRequested += OnPermissionRequested;
            connection.ElicitationRequested += OnElicitationRequested;
            connection.FileReadRequested += OnFileReadRequested;
            connection.FileWriteRequested += OnFileWriteRequested;
            connection.Disconnected += OnDisconnected;
            try
            {
                await connection.InitializeAsync(cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(connection, _connection) || NeedsAuthentication)
                    throw new InvalidOperationException("The agent disconnected before the session was ready.");
                _pendingCommandCatalogs = new Dictionary<string, IReadOnlyList<AvailableCommand>>(StringComparer.Ordinal);
                var session = await connection.NewSessionAsync(_services.WorkspaceRoot ?? Environment.CurrentDirectory, null, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(connection, _connection) || NeedsAuthentication)
                    throw new InvalidOperationException("The agent disconnected before the session was ready.");
                _sessionId = session.SessionId;
                if (_pendingCommandCatalogs.TryGetValue(session.SessionId, out var commands)) ApplyCommandCatalog(commands);
                _pendingCommandCatalogs = null;
                IsSignedIn = true;
                NeedsAuthentication = false;
                ApplyConfigOptions(session.ConfigOptions);
                StatusMessage = null;
                return (connection, session.SessionId);
            }
            catch
            {
                if (ReferenceEquals(connection, _connection)) await ReleaseConnectionAsync().ConfigureAwait(true);
                throw;
            }
            finally
            {
                _pendingCommandCatalogs = null;
            }
        }
        finally
        {
            IsConnecting = false;
            try { _connectGate.Release(); } catch (ObjectDisposedException) { }
            NotifyStateChanged();
        }
    }

    private void ApplyConfigOptions(IReadOnlyList<SessionConfigOption> options)
    {
        _modelOption = options.FirstOrDefault(option => option.Category == "model")
            ?? options.FirstOrDefault(option => option.Id == "model");
        _effortOption = options.FirstOrDefault(option => option.Category == "thought_level")
            ?? options.FirstOrDefault(option => option.Id == "effort");
        ReplaceOptions(AvailableModels, _modelOption);
        ReplaceOptions(AvailableEfforts, _effortOption);
        _selectedModel = AvailableModels.FirstOrDefault(option => option.Value == _modelOption?.CurrentValue);
        _selectedEffort = AvailableEfforts.FirstOrDefault(option => option.Value == _effortOption?.CurrentValue);
        NotifySelectionsChanged();
        OnPropertyChanged(nameof(HasEffort));
        NotifyStateChanged();
    }

    private static void ReplaceOptions(ObservableCollection<SessionConfigValue> target, SessionConfigOption? option)
    {
        target.Clear();
        if (option is null) return;
        foreach (var value in option.Options) target.Add(value);
    }

    private void NotifySelectionsChanged()
    {
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedEffort));
        OnPropertyChanged(nameof(ActiveModelName));
        OnPropertyChanged(nameof(ActiveEffortName));
    }

    private void OnSessionUpdate(object? sender, SessionUpdateEventArgs e)
    {
        // Capture eligibility at notification arrival, not after a queued UI dispatch.
        // Only session/new may publish a catalog before the session ID is known.
        var pendingCatalogs = _pendingCommandCatalogs;
        var belongsToKnownSession = _sessionId is not null && e.SessionId == _sessionId;
        RunOnUi(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _connection)) return;
            if (e.Update is SessionUpdate.AvailableCommandsChanged catalog)
            {
                if (e.SessionId == _sessionId && (belongsToKnownSession || pendingCatalogs is not null))
                    ApplyCommandCatalog(catalog.Commands);
                else if (pendingCatalogs is not null && ReferenceEquals(pendingCatalogs, _pendingCommandCatalogs))
                    pendingCatalogs[e.SessionId] = catalog.Commands;
                return;
            }
            if (e.SessionId != _sessionId) return;
            switch (e.Update)
            {
                case SessionUpdate.ConfigOptionsChanged config:
                    ApplyConfigOptions(config.ConfigOptions);
                    break;
                case SessionUpdate.UserMessageChunk chunk:
                    EnsureUserMessage().AppendText(chunk.Text);
                    break;
                case SessionUpdate.AgentMessageChunk chunk:
                    EnsureAssistantMessage().AppendText(chunk.Text);
                    UpdateActivity("Responding…");
                    break;
                case SessionUpdate.AgentThoughtChunk:
                    UpdateActivity("Thinking…");
                    break;
                case SessionUpdate.ToolCall toolCall:
                    UpsertToolCall(toolCall.Call);
                    break;
                case SessionUpdate.Plan plan:
                    CurrentPlan = new PlanViewModel(plan.Entries);
                    break;
                case SessionUpdate.TurnEnded:
                    // The prompt task owns IsBusy, preventing a new send/config before it returns.
                    _currentAssistantMessage = null;
                    _currentUserMessage = null;
                    UpdateActivity("Working…");
                    break;
            }
        });
    }

    private void UpsertToolCall(ToolCallUpdate call)
    {
        var message = EnsureAssistantMessage();
        var existing = message.ToolCalls.FirstOrDefault(t => t.ToolCallId == call.ToolCallId);
        if (existing is not null) existing.Apply(call);
        else message.ToolCalls.Add(new ToolCallCardViewModel(call));
        var title = existing?.Title ?? call.Title;
        UpdateActivity((call.Status == ToolCallStatus.Pending || call.Status == ToolCallStatus.InProgress) &&
            !string.IsNullOrWhiteSpace(title) ? title : "Working…");
    }

    private ChatMessageViewModel EnsureAssistantMessage()
    {
        _currentUserMessage = null; // an agent turn starting closes off any replayed user bubble.
        if (_currentAssistantMessage is null)
        {
            _currentAssistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
            Messages.Add(_currentAssistantMessage);
        }
        return _currentAssistantMessage;
    }

    private ChatMessageViewModel EnsureUserMessage()
    {
        _currentAssistantMessage = null; // a replayed user turn starting closes off the prior assistant bubble.
        if (_currentUserMessage is null)
        {
            _currentUserMessage = new ChatMessageViewModel(ChatRole.User);
            Messages.Add(_currentUserMessage);
        }
        return _currentUserMessage;
    }

    private void OnPermissionRequested(object? sender, PermissionRequestEventArgs e)
    {
        RunOnUi(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _connection) || e.SessionId != _sessionId)
            {
                e.Response.TrySetException(new OperationCanceledException("The permission request no longer belongs to an active session."));
                return;
            }
            _pendingPermissionResponse?.TrySetException(new OperationCanceledException("Superseded by a newer permission request."));
            _pendingPermissionResponse = e.Response;
            PendingPermission = new PermissionRequestViewModel(e.Call.Title, e.Options, option =>
            {
                e.Response.TrySetResult(option.OptionId);
                _pendingPermissionResponse = null;
                PendingPermission = null;
                UpdateActivity("Working…");
            });
            UpdateActivity("Waiting for permission…");
        });
    }

    private void OnElicitationRequested(object? sender, ElicitationRequestEventArgs e)
    {
        RunOnUi(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _connection) || e.SessionId != _sessionId)
            {
                e.Response.TrySetException(new OperationCanceledException("The elicitation request no longer belongs to an active session."));
                return;
            }
            _pendingElicitationResponse?.TrySetResult(new ElicitationAnswer(ElicitationAction.Cancel, _emptyElicitationContent));
            _pendingElicitationResponse = e.Response;
            PendingElicitation = new ElicitationRequestViewModel(e.Message, e.Fields, answer =>
            {
                e.Response.TrySetResult(answer);
                _pendingElicitationResponse = null;
                PendingElicitation = null;
            });
        });
    }

    private async void OnFileReadRequested(object? sender, FileReadRequestEventArgs e)
    {
        try
        {
            if (!WorkspacePathGuard.TryResolveWithinWorkspace(_services.WorkspaceRoot, e.Path, out var fullPath))
            {
                e.Response.TrySetException(new UnauthorizedAccessException($"Path '{e.Path}' is outside the workspace."));
                return;
            }

            var liveText = await _services.TryReadOpenDocumentAsync(fullPath, _lifetime.Token).ConfigureAwait(true);
            var text = liveText ?? File.ReadAllText(fullPath);

            if (e.Line.HasValue || e.Limit.HasValue)
            {
                var lines = SplitPreservingLineEndings(text);
                var start = Math.Max(0, (e.Line ?? 1) - 1);
                var count = e.Limit ?? Math.Max(0, lines.Length - start);
                text = string.Join("\n", lines.Skip(start).Take(count));
            }

            e.Response.TrySetResult(text);
        }
        catch (Exception ex) { e.Response.TrySetException(ex); }
    }

    private static string[] SplitPreservingLineEndings(string text) => (text ?? string.Empty).Split('\n');

    private async void OnFileWriteRequested(object? sender, FileWriteRequestEventArgs e)
    {
        try
        {
            if (!WorkspacePathGuard.TryResolveWithinWorkspace(_services.WorkspaceRoot, e.Path, out var fullPath))
            {
                e.Response.TrySetException(new UnauthorizedAccessException($"Path '{e.Path}' is outside the workspace."));
                return;
            }

            if (await _services.TryWriteOpenDocumentAsync(fullPath, e.Content, _lifetime.Token).ConfigureAwait(true))
            {
                e.Response.TrySetResult(true);
                return;
            }

            WriteFilePreservingEncoding(fullPath, e.Content);
            e.Response.TrySetResult(true);
        }
        catch (Exception ex) { e.Response.TrySetException(ex); }
    }

    private static void WriteFilePreservingEncoding(string fullPath, string content)
    {
        var encoding = File.Exists(fullPath) ? DetectEncodingFromBom(fullPath) : new UTF8Encoding(false);
        var tempPath = fullPath + ".tmp" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, content, encoding);
            if (File.Exists(fullPath)) File.Replace(tempPath, fullPath, null);
            else File.Move(tempPath, fullPath);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private static Encoding DetectEncodingFromBom(string path)
    {
        var buffer = new byte[4];
        int read;
        using (var stream = File.OpenRead(path))
        {
            read = stream.Read(buffer, 0, buffer.Length);
        }

        if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return new UTF8Encoding(true);
        if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) return Encoding.BigEndianUnicode;
        return new UTF8Encoding(false);
    }

    private void OnDisconnected(object? sender, Exception? ex)
    {
        RunOnUi(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _connection)) return;
            _ = ReleaseConnectionAsync();
            StatusMessage = ex is not null ? $"Agent disconnected: {ex.Message}" : "Agent disconnected.";
        });
    }

    private async Task ReleaseConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        _sessionId = null;
        _pendingCommandCatalogs = null;
        _availableCommands = Array.Empty<AvailableCommand>();
        _hasCommandCatalog = false;
        RefreshSlashSuggestions();
        ActivityText = string.Empty;
        IsSignedIn = _services.AuthService.CurrentState == AuthState.SignedIn;
        PendingPermission = null;
        _pendingPermissionResponse = null;
        CurrentPlan = null;
        ApplyConfigOptions(Array.Empty<SessionConfigOption>());
        if (connection is null) return;
        connection.SessionUpdate -= OnSessionUpdate;
        connection.PermissionRequested -= OnPermissionRequested;
        connection.ElicitationRequested -= OnElicitationRequested;
        connection.FileReadRequested -= OnFileReadRequested;
        connection.FileWriteRequested -= OnFileWriteRequested;
        connection.Disconnected -= OnDisconnected;
        try { await connection.DisposeAsync().ConfigureAwait(true); }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not close the agent: {ex.Message}";
        }
    }

    private void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e)
    {
        RunOnUi(() =>
        {
            if (_disposed) return;
            ApplyAuthState(e.State, e.Detail);
            if (!NeedsAuthentication && _sessionId is null && !IsConnecting) _ = InitializeAsync();
            else if (NeedsAuthentication) _ = ReleaseConnectionAsync();
        });
    }

    private void ApplyAuthState(AuthState state, string? detail = null)
    {
        IsSignedIn = state == AuthState.SignedIn || (state != AuthState.SignedOut && _sessionId is not null);
        NeedsAuthentication = state == AuthState.SignedOut;
        if (detail is not null) StatusMessage = detail;
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanConfigure));
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SignInCommand.NotifyCanExecuteChanged();
        RemoveAttachmentCommand.NotifyCanExecuteChanged();
        AttachActiveDocumentCommand.NotifyCanExecuteChanged();
        NewSessionCommand.NotifyCanExecuteChanged();
        ShowHistoryCommand.NotifyCanExecuteChanged();
        OpenSessionCommand.NotifyCanExecuteChanged();
        UpdateSlashPresentation();
    }

    private void RunOnUi(Action action)
    {
        if (_uiContext is not null && _uiContext != SynchronizationContext.Current) _uiContext.Post(_ => action(), null);
        else action();
    }

    private Task OnUiAsync(Func<Task> action)
    {
        if (_uiContext is null || _uiContext == SynchronizationContext.Current) return action();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(async _ =>
        {
            try { await action().ConfigureAwait(true); completion.TrySetResult(true); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, null);
        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _services.AuthService.StateChanged -= OnAuthStateChanged;
        _lifetime.Cancel();
        RunOnUi(() => _ = DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await ReleaseConnectionAsync().ConfigureAwait(true);
        }
        finally
        {
            NotifyStateChanged();
            // EnsureConnectedAsync's finally tolerates a disposed gate (ObjectDisposedException is
            // swallowed there), so a late in-flight continuation racing this disposal cannot throw unhandled.
            _connectGate.Dispose();
            _lifetime.Dispose();
        }
    }
}
