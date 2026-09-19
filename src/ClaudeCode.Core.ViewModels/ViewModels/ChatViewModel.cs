using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private SessionConfigOption? _modeOption;
    private SessionConfigValue? _selectedModel;
    private SessionConfigValue? _selectedEffort;
    private SessionConfigValue? _selectedMode;
    private ChatMessageViewModel? _currentAssistantMessage;
    private DateTimeOffset? _turnStartedAt;
    private ChatMessageViewModel? _currentUserMessage;
    private bool _isHistoryOpen;
    private bool _isHistoryLoading;
    private string? _historyError;
    private string _historyFilter = string.Empty;
    private List<SessionSummary> _allSessionHistory = new List<SessionSummary>();
    private string _sessionTitle = UntitledSessionTitle;
    private long _sessionUsedTokens;
    private long _turnStartUsedTokens;
    private long? _turnTokens;
    private long? _contextWindowSize;
    private string? _explicitSessionTitle;
    private const string UntitledSessionTitle = "Untitled";
    private const int MaxSessionTitleLength = 80;
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
    private UsageSnapshot? _usage;
    private UsageWarningViewModel? _usageWarning;
    private bool _usageWarningDismissed;
    private int _lastUsageWarningPercent = -1;
    private bool _isUsagePanelOpen;

    private const long MaxImageAttachmentBytes = 5L * 1024 * 1024;
    private const long MaxDocumentAttachmentBytes = 1L * 1024 * 1024;
    private const int UsageWarningThresholdPercent = 75;
    private static readonly TimeSpan UsagePollInterval = TimeSpan.FromMinutes(5);

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
        AttachActiveDocumentCommand = new AsyncRelayCommand(AttachActiveDocumentAsync, () => CanEditDraft && !_isCapturingDocument && _services.HasActiveDocument);
        _services.ActiveDocumentChanged += OnActiveDocumentChanged;
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
        AcceptAllChangesCommand = new AsyncRelayCommand(() => OnUiAsync(async () =>
        {
            foreach (var file in ChangedFiles.ToList()) await AcceptChangeAsync(file).ConfigureAwait(true);
        }));
        RejectAllChangesCommand = new AsyncRelayCommand(() => OnUiAsync(async () =>
        {
            foreach (var file in ChangedFiles.ToList()) await RejectChangeAsync(file).ConfigureAwait(true);
        }));
        OpenChangedFileCommand = new AsyncRelayCommand<ChangedFileViewModel>(file =>
            file is null ? Task.CompletedTask : _services.OpenDocumentAsync(file.FullPath, _lifetime.Token));
        ToggleRemoteControlCommand = new AsyncRelayCommand(ToggleRemoteControlAsync, () => CanConfigure && !_isRemoteControlBusy);
        OpenUsagePanelCommand = new RelayCommand(() => IsUsagePanelOpen = true);
        CloseUsagePanelCommand = new RelayCommand(() => IsUsagePanelOpen = false);
        DismissUsageWarningCommand = new RelayCommand(DismissUsageWarning);
        _services.AuthService.StateChanged += OnAuthStateChanged;
        ApplyAuthState(_services.AuthService.CurrentState);
        Initialization = InitializeAsync();
        _ = UsagePollingLoopAsync();
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new ObservableCollection<ChatMessageViewModel>();
    public ObservableCollection<ChatAttachmentViewModel> Attachments { get; } = new ObservableCollection<ChatAttachmentViewModel>();
    public ObservableCollection<SessionConfigValue> AvailableModels { get; } = new ObservableCollection<SessionConfigValue>();
    public ObservableCollection<SessionConfigValue> AvailableEfforts { get; } = new ObservableCollection<SessionConfigValue>();
    public ObservableCollection<SessionConfigValue> AvailableModes { get; } = new ObservableCollection<SessionConfigValue>();
    public ObservableCollection<AvailableCommand> SlashSuggestions { get; } = new ObservableCollection<AvailableCommand>();
    public ObservableCollection<SessionSummary> SessionHistory { get; } = new ObservableCollection<SessionSummary>();
    public ObservableCollection<ChangedFileViewModel> ChangedFiles { get; } = new ObservableCollection<ChangedFileViewModel>();
    public IAsyncRelayCommand AcceptAllChangesCommand { get; }
    public IAsyncRelayCommand RejectAllChangesCommand { get; }
    public IAsyncRelayCommand<ChangedFileViewModel> OpenChangedFileCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand SignInCommand { get; }
    public IAsyncRelayCommand NewSessionCommand { get; }
    public IAsyncRelayCommand ShowHistoryCommand { get; }
    public IAsyncRelayCommand<SessionSummary> OpenSessionCommand { get; }
    public IRelayCommand<ChatAttachmentViewModel> RemoveAttachmentCommand { get; }
    public IAsyncRelayCommand AttachActiveDocumentCommand { get; }
    public IRelayCommand<AvailableCommand> ApplySlashSuggestionCommand { get; }
    public IAsyncRelayCommand ToggleRemoteControlCommand { get; }
    public IRelayCommand OpenUsagePanelCommand { get; }

    private bool _isRemoteControlEnabled;
    private bool _isRemoteControlBusy;
    private string? _remoteControlUrl;

    /// <summary>True while the current session can be driven from claude.ai/code.</summary>
    public bool IsRemoteControlEnabled
    {
        get => _isRemoteControlEnabled;
        private set => SetProperty(ref _isRemoteControlEnabled, value);
    }

    public bool IsRemoteControlBusy
    {
        get => _isRemoteControlBusy;
        private set
        {
            if (SetProperty(ref _isRemoteControlBusy, value)) ToggleRemoteControlCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Link to this session on claude.ai/code while Remote Control is on.</summary>
    public string? RemoteControlUrl
    {
        get => _remoteControlUrl;
        private set => SetProperty(ref _remoteControlUrl, value);
    }

    public Task ToggleRemoteControlAsync() => OnUiAsync(() => SetRemoteControlAsync(!IsRemoteControlEnabled));

    private async Task SetRemoteControlAsync(bool enabled)
    {
        var connection = _connection;
        var sessionId = _sessionId;
        if (connection is null || sessionId is null || _isRemoteControlBusy || _disposed) return;
        IsRemoteControlBusy = true;
        try
        {
            var state = await connection.SetRemoteControlAsync(sessionId, enabled, RemoteControlSessionName, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection) || sessionId != _sessionId) return;
            IsRemoteControlEnabled = state.Enabled;
            RemoteControlUrl = state.Enabled ? state.SessionUrl : null;
            StatusMessage = null;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Remote Control: {ex.Message}";
        }
        finally
        {
            IsRemoteControlBusy = false;
        }
    }

    private string RemoteControlSessionName
    {
        get
        {
            var root = _services.WorkspaceRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = string.IsNullOrEmpty(root) ? null : Path.GetFileName(root);
            return "Visual Studio · " + (string.IsNullOrEmpty(name) ? "workspace" : name);
        }
    }

    // A new or resumed session starts with Remote Control off; the option turns it on right away.
    private void OnSessionStarted()
    {
        IsRemoteControlEnabled = false;
        RemoteControlUrl = null;
        if (_services.RemoteControlAtStartup) _ = SetRemoteControlAsync(true);
    }
    public IRelayCommand CloseUsagePanelCommand { get; }
    public IRelayCommand DismissUsageWarningCommand { get; }
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
    // Deliberately does not require !IsBusy: SetSessionConfigOptionAsync is its own ACP RPC call
    // over the same JSON-RPC connection as an in-flight prompt, which already supports concurrent
    // in-flight requests (matched by request id) - there's no protocol reason model/mode/effort
    // can't change mid-turn, and other clients (the reference VS Code extension, the CLI) let you.
    public bool CanConfigure => !_disposed && !NeedsAuthentication && !IsConnecting && !IsConfigBusy &&
        !_isCapturingDocument && _sessionId is not null;
    public bool HasEffort => AvailableEfforts.Count > 0;
    public bool HasModes => AvailableModes.Count > 0;
    public string ActiveModelName => _selectedModel?.Name ?? "Model unavailable";
    public string ActiveEffortName => _selectedEffort?.Name ?? string.Empty;
    public string ModelEffortLabel => HasEffort && ActiveEffortName.Length > 0 ? ActiveModelName + " · " + ActiveEffortName : ActiveModelName;
    public string ActiveModeName => _selectedMode?.Name ?? "Mode unavailable";

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

    public SessionConfigValue? SelectedMode
    {
        get => _selectedMode;
        set => _ = SelectModeAsync(value);
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

    public string SessionTitle
    {
        get => _sessionTitle;
        private set => SetProperty(ref _sessionTitle, value);
    }

    /// <summary>Tokens consumed so far by the in-flight turn (delta of the agent's usage_update since
    /// the prompt was sent), or null until the agent reports usage.</summary>
    public long? TurnTokens
    {
        get => _turnTokens;
        private set => SetProperty(ref _turnTokens, value);
    }

    public long SessionUsedTokens => _sessionUsedTokens;

    public long? ContextWindowSize => _contextWindowSize;

    /// <summary>How full the context window is, 0–100, or null until the agent reports both numbers.</summary>
    public int? ContextUsagePercent => _contextWindowSize is long size && size > 0
        ? (int)Math.Min(100, Math.Round(100.0 * _sessionUsedTokens / size))
        : null;

    public string ContextUsageLabel => _contextWindowSize is long size && size > 0
        ? $"Context: {FormatTokens(_sessionUsedTokens)} / {FormatTokens(size)} ({ContextUsagePercent}%)"
        : "Context usage unknown";

    private static string FormatTokens(long count) =>
        count < 1000 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : count < 1_000_000 ? (count / 1000.0).ToString(count < 10_000 ? "0.#" : "0", System.Globalization.CultureInfo.InvariantCulture) + "k"
        : (count / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";

    private void UpdateSessionTitleFromFirstUserMessage()
    {
        if (_explicitSessionTitle is not null || Messages.Count == 0 || Messages[0].Role != ChatRole.User) return;
        var firstLine = Messages[0].Text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        if (firstLine.Length == 0) return;
        SessionTitle = firstLine.Length > MaxSessionTitleLength ? firstLine.Substring(0, MaxSessionTitleLength - 1) + "…" : firstLine;
    }

    public string HistoryFilter
    {
        get => _historyFilter;
        set
        {
            if (SetProperty(ref _historyFilter, value ?? string.Empty)) RefreshHistoryFilter();
        }
    }

    public void CloseHistory() => RunOnUi(() => IsHistoryOpen = false);

    private void RefreshHistoryFilter()
    {
        var filter = _historyFilter.Trim();
        SessionHistory.Clear();
        foreach (var session in _allSessionHistory)
        {
            if (filter.Length == 0
                || (session.Title?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || session.SessionId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SessionHistory.Add(session);
            }
        }
    }

    private async Task UsagePollingLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                UsageSnapshot? snapshot = await _services.UsageService.GetUsageAsync(_lifetime.Token).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    RunOnUi(() => ApplyUsageSnapshot(snapshot));
                }
            }
            catch (OperationCanceledException) when (_disposed || _lifetime.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Usage is best-effort presentation, never allowed to affect the chat session itself.
            }

            try
            {
                await Task.Delay(UsagePollInterval, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ApplyUsageSnapshot(UsageSnapshot snapshot)
    {
        Usage = snapshot;

        UsageLimit? top = snapshot.Limits
            .Where(limit => limit.Percent >= UsageWarningThresholdPercent)
            .OrderByDescending(limit => limit.Percent)
            .FirstOrDefault();
        if (top is null)
        {
            UsageWarning = null;
            _usageWarningDismissed = false;
            _lastUsageWarningPercent = -1;
            return;
        }

        // A dismissal stays in effect until usage climbs past where it was when dismissed -
        // otherwise the very next poll (a few minutes later, same percent) would just reopen it.
        if (_usageWarningDismissed && top.Percent <= _lastUsageWarningPercent)
        {
            return;
        }

        _usageWarningDismissed = false;
        _lastUsageWarningPercent = top.Percent;
        UsageWarning = new UsageWarningViewModel(FormatUsageWarningMessage(top));
    }

    public void DismissUsageWarning()
    {
        _usageWarningDismissed = true;
        UsageWarning = null;
    }

    private UsageLimitDisplay? BuildUsageDisplay(string label, Func<UsageLimit, bool> matches)
    {
        UsageLimit? limit = _usage?.Limits.FirstOrDefault(matches);
        if (limit is null)
        {
            return null;
        }

        string? resetText = limit.ResetsAt is DateTimeOffset resetsAt ? FormatResetsAtLabel(resetsAt) : null;
        return new UsageLimitDisplay(label, limit.Percent, resetText, limit.Percent >= UsageWarningThresholdPercent);
    }

    private static string FormatUsageWarningMessage(UsageLimit limit)
    {
        string label = limit.Kind switch
        {
            "session" => "session limit",
            "weekly_all" => "weekly limit",
            "weekly_scoped" => limit.ScopeLabel is string scope ? $"weekly {scope} limit" : "weekly limit",
            _ => "usage limit",
        };
        string reset = limit.ResetsAt is DateTimeOffset resetsAt ? $" · {FormatResetsInLabel(resetsAt)}" : "";
        return $"You've used {limit.Percent}% of your {label}{reset}.";
    }

    private static string FormatResetsInLabel(DateTimeOffset resetsAt)
    {
        TimeSpan remaining = resetsAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "resets soon";
        if (remaining.TotalDays >= 1) return $"resets in {(int)remaining.TotalDays}d";
        if (remaining.TotalHours >= 1) return $"resets in {(int)remaining.TotalHours}h";
        return $"resets in {Math.Max(1, (int)remaining.TotalMinutes)}m";
    }

    private static string FormatResetsAtLabel(DateTimeOffset resetsAt)
    {
        DateTimeOffset local = resetsAt.ToLocalTime();
        TimeSpan remaining = resetsAt - DateTimeOffset.UtcNow;
        return remaining < TimeSpan.FromHours(20)
            ? $"Resets {local:h:mm tt}"
            : $"Resets {local:dddd h:mm tt}";
    }

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

    /// <summary>Raw snapshot; non-null once the first usage fetch succeeds. Drives the usage
    /// toolbar button's visibility - there is nothing useful to show before that.</summary>
    public UsageSnapshot? Usage
    {
        get => _usage;
        private set
        {
            if (SetProperty(ref _usage, value))
            {
                OnPropertyChanged(nameof(SessionUsage));
                OnPropertyChanged(nameof(WeeklyUsage));
                OnPropertyChanged(nameof(FableUsage));
            }
        }
    }

    public UsageLimitDisplay? SessionUsage => BuildUsageDisplay("Current session", limit => limit.Kind == "session");

    public UsageLimitDisplay? WeeklyUsage => BuildUsageDisplay("This week", limit => limit.Kind == "weekly_all");

    public UsageLimitDisplay? FableUsage => BuildUsageDisplay(
        (_usage?.Limits.FirstOrDefault(limit => limit.Kind == "weekly_scoped")?.ScopeLabel ?? "Fable") + " this week",
        limit => limit.Kind == "weekly_scoped");

    public UsageWarningViewModel? UsageWarning
    {
        get => _usageWarning;
        private set => SetProperty(ref _usageWarning, value);
    }

    public bool IsUsagePanelOpen
    {
        get => _isUsagePanelOpen;
        set
        {
            if (SetProperty(ref _isUsagePanelOpen, value) && value) _ = RefreshUsageAsync();
        }
    }

    private async Task RefreshUsageAsync()
    {
        try
        {
            UsageSnapshot? snapshot = await _services.UsageService.GetUsageAsync(_lifetime.Token).ConfigureAwait(false);
            if (snapshot is not null && !_disposed) RunOnUi(() => ApplyUsageSnapshot(snapshot));
        }
        catch
        {
            // Usage is best-effort presentation, never allowed to affect the chat session itself.
        }
    }

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

    private void OnActiveDocumentChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_disposed) return;
        AttachActiveDocumentCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasActiveDocument));
    });

    public bool HasActiveDocument => _services.HasActiveDocument;

    public void DismissAttachmentError() => RunOnUi(() => AttachmentError = null);

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
    public Task SelectModeAsync(SessionConfigValue? value) => OnUiAsync(() => ChangeConfigAsync(_modeOption, value));

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
        _turnStartedAt = DateTimeOffset.UtcNow;
        _turnStartUsedTokens = _sessionUsedTokens;
        TurnTokens = null;
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

            // Images render as thumbnails on the bubble; only documents keep a text placeholder.
            var transcriptText = string.Join(Environment.NewLine, new[] { text }
                .Where(part => part.Length > 0).Concat(attachments.Where(attachment => attachment.IsDocument)
                    .Select(attachment => "[Document: " + attachment.Name + "]")));
            Messages.Add(new ChatMessageViewModel(ChatRole.User, transcriptText)
            {
                Images = attachments.Where(attachment => attachment.IsImage)
                    .Select(attachment => new ChatMessageImage(attachment.Name, attachment.MimeType, attachment.Base64Data)).ToList(),
            });
            UpdateSessionTitleFromFirstUserMessage();
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

        SendPendingPlanReview();
    }

    private string? _pendingPlanReviewComments;
    private PlanReviewViewModel? _pendingPlan;

    /// <summary>The implementation plan currently awaiting Proceed/Review, kept (resolved) until the
    /// next plan or session change so a plan document can keep showing it.</summary>
    public PlanReviewViewModel? PendingPlan
    {
        get => _pendingPlan;
        private set => SetProperty(ref _pendingPlan, value);
    }

    /// <summary>Raised when the agent asks for plan approval; hosts open the plan document on it.</summary>
    public event EventHandler? PlanReviewRequested;

    /// <summary>Raised when the conversation needs the user back (turn finished, permission or plan
    /// review pending); hosts may show a system notification if the IDE is in the background.</summary>
    public event EventHandler<ChatAttentionEventArgs>? AttentionRequested;

    private void RaiseAttention(ChatAttentionKind kind, string title, string message) =>
        AttentionRequested?.Invoke(this, new ChatAttentionEventArgs(kind, title, Truncate(message, 160)));

    private static string Truncate(string text, int max)
    {
        var firstLine = (text ?? string.Empty).Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        return firstLine.Length <= max ? firstLine : firstLine.Substring(0, max - 1) + "…";
    }

    // Review comments are delivered as the next prompt once the rejected plan turn has finished.
    private void SendPendingPlanReview()
    {
        var comments = _pendingPlanReviewComments;
        if (comments is null || _disposed || !CanEditDraft) return;
        _pendingPlanReviewComments = null;
        InputText = "Review comments on the plan:\n" + comments;
        if (CanSend()) _ = SendAsync();
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
            OnSessionStarted();
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
        _historyFilter = string.Empty;
        OnPropertyChanged(nameof(HistoryFilter));
        try
        {
            var (connection, _) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            var cwd = _services.WorkspaceRoot ?? Environment.CurrentDirectory;
            var sessions = await connection.ListSessionsAsync(cwd, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            _allSessionHistory = sessions.ToList();
            RefreshHistoryFilter();
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
            _explicitSessionTitle = string.IsNullOrWhiteSpace(session.Title) ? null : session.Title;
            if (_explicitSessionTitle is not null) SessionTitle = _explicitSessionTitle;
            // Known upfront (unlike session/new): set it before the call below so replayed
            // session/update notifications, tagged with this id, are not dropped by OnSessionUpdate's
            // "belongs to the known session" check while the request is still in flight.
            _sessionId = session.SessionId;
            var result = await connection.LoadSessionAsync(session.SessionId, session.Cwd, null, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            ApplyConfigOptions(result.ConfigOptions);
            OnSessionStarted();
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
        lock (_changedFilesByPath) _changedFilesByPath.Clear();
        ChangedFiles.Clear();
        PendingPlan = null;
        _pendingPlanReviewComments = null;
        _explicitSessionTitle = null;
        SessionTitle = UntitledSessionTitle;
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
            OnSessionStarted();
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
        _modeOption = options.FirstOrDefault(option => option.Category == "mode")
            ?? options.FirstOrDefault(option => option.Id == "mode");
        ReplaceOptions(AvailableModels, _modelOption);
        ReplaceOptions(AvailableEfforts, _effortOption);
        ReplaceOptions(AvailableModes, _modeOption);
        _selectedModel = AvailableModels.FirstOrDefault(option => option.Value == _modelOption?.CurrentValue);
        _selectedEffort = AvailableEfforts.FirstOrDefault(option => option.Value == _effortOption?.CurrentValue);
        _selectedMode = AvailableModes.FirstOrDefault(option => option.Value == _modeOption?.CurrentValue);
        NotifySelectionsChanged();
        OnPropertyChanged(nameof(HasEffort));
        OnPropertyChanged(nameof(HasModes));
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
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(ActiveModelName));
        OnPropertyChanged(nameof(ActiveEffortName));
        OnPropertyChanged(nameof(ModelEffortLabel));
        OnPropertyChanged(nameof(ActiveModeName));
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
                    UpdateSessionTitleFromFirstUserMessage();
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
                case SessionUpdate.UsageUpdate usage:
                    _sessionUsedTokens = usage.UsedTokens;
                    if (usage.ContextWindowSize is long windowSize) _contextWindowSize = windowSize;
                    if (IsBusy) TurnTokens = Math.Max(0, usage.UsedTokens - _turnStartUsedTokens);
                    OnPropertyChanged(nameof(SessionUsedTokens));
                    OnPropertyChanged(nameof(ContextWindowSize));
                    OnPropertyChanged(nameof(ContextUsagePercent));
                    OnPropertyChanged(nameof(ContextUsageLabel));
                    break;
                case SessionUpdate.TurnEnded:
                    // The prompt task owns IsBusy, preventing a new send/config before it returns.
                    if (_currentAssistantMessage is not null && _turnStartedAt is DateTimeOffset startedAt)
                    {
                        _currentAssistantMessage.DurationSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds);
                    }

                    if (_currentAssistantMessage is not null && TurnTokens is long turnTokens) _currentAssistantMessage.TokensUsed = turnTokens;
                    RaiseAttention(ChatAttentionKind.TurnCompleted, "Claude finished",
                        _currentAssistantMessage?.Text is { Length: > 0 } reply ? reply : "The response is ready in Visual Studio.");

                    _currentAssistantMessage = null;
                    _currentUserMessage = null;
                    _turnStartedAt = null;
                    UpdateActivity("Working…");
                    _ = RefreshUsageAsync();
                    break;
            }
        });
    }

    private void UpsertToolCall(ToolCallUpdate call)
    {
        _ = TrackToolCallFileChangesAsync(call);
        var message = EnsureAssistantMessage();
        var existing = message.ToolCalls.FirstOrDefault(t => t.ToolCallId == call.ToolCallId);
        if (existing is not null)
        {
            existing.Apply(call);
        }
        else
        {
            var card = new ToolCallCardViewModel(call);
            message.ToolCalls.Add(card);
            message.AppendToolCall(card);
        }
        // Deliberately not the tool's own title/command text here: the activity indicator is a
        // generic "something is happening" status, not a live command echo.
        UpdateActivity("Working…");
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
            void Choose(PermissionOption option)
            {
                e.Response.TrySetResult(option.OptionId);
                _pendingPermissionResponse = null;
                PendingPermission = null;
                UpdateActivity("Working…");
            }

            PendingPermission = new PermissionRequestViewModel(e.Call.Title, e.Options, Choose);
            UpdateActivity("Waiting for permission…");

            // ExitPlanMode arrives as a switch_mode tool call whose content is the plan markdown.
            var planText = e.Call.Kind == "switch_mode"
                ? string.Join("\n", e.Call.Content.Where(content => !content.IsDiff && !string.IsNullOrWhiteSpace(content.Text)).Select(content => content.Text))
                : string.Empty;
            if (planText.Length > 0)
            {
                PlanReviewViewModel? plan = null;
                plan = new PlanReviewViewModel(planText, e.Options,
                    option =>
                    {
                        Choose(option);
                        plan!.MarkResolved("Plan accepted — implementing…");
                    },
                    comments =>
                    {
                        if (plan!.RejectOption is null) return;
                        _pendingPlanReviewComments = comments;
                        Choose(plan.RejectOption);
                        plan.MarkResolved("Sent back for revision");
                        if (!IsBusy) SendPendingPlanReview();
                    });
                PendingPlan = plan;
                PlanReviewRequested?.Invoke(this, EventArgs.Empty);
                RaiseAttention(ChatAttentionKind.PlanReview, "Claude has a plan for you", "Review or approve the implementation plan.");
            }
            else
            {
                RaiseAttention(ChatAttentionKind.PermissionNeeded, "Claude needs your permission", e.Call.Title);
            }
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
            using var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, e.Path);
            string? liveText = null;
            using (var document = pathLease.ProtectDocument())
            {
                if (document is not null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    liveText = await _services.TryReadOpenDocumentAsync(pathLease.FullPath, _lifetime.Token).ConfigureAwait(true);
            }
            var text = liveText ?? pathLease.ReadAllText();

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
            // Separate leases: ReadAllText hands its file handle to a FileStream, which disposes it,
            // so a lease that has been read from cannot be written through afterwards.
            var tracked = await TrackChangeBeforeWriteAsync(e.Path).ConfigureAwait(true);
            using var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, e.Path);
            await WriteLeasedFileAsync(pathLease, e.Content).ConfigureAwait(true);
            RunOnUi(() => tracked.UpdateCounts(e.Content));
            e.Response.TrySetResult(true);
        }
        catch (Exception ex) { e.Response.TrySetException(ex); }
    }

    private readonly Dictionary<string, ChangedFileViewModel> _changedFilesByPath = new Dictionary<string, ChangedFileViewModel>(StringComparer.OrdinalIgnoreCase);

    // The agent process writes Edit/Write results to disk itself (the client fs is not used for
    // them), so track those files from their tool-call diffs: snapshot the original while the call is
    // still pending (before the file changes), refresh the +/- counts once it completes.
    private async Task TrackToolCallFileChangesAsync(ToolCallUpdate call)
    {
        // Only live turns: a resumed session replays old, already-applied tool calls whose "original"
        // would be the current file - nothing to revert, and 50 phantom rows in the panel.
        if (!IsBusy) return;
        try
        {
            foreach (var content in call.Content)
            {
                if (!content.IsDiff || string.IsNullOrWhiteSpace(content.Path)) continue;
                ChangedFileViewModel tracked;
                try { tracked = await TrackChangeBeforeWriteAsync(content.Path!).ConfigureAwait(true); }
                catch (Exception) { continue; } // outside the workspace or unreadable: not ours to revert.

                if (call.Status == ToolCallStatus.Completed)
                {
                    string? current;
                    using (var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, tracked.FullPath))
                        current = await ReadLeasedFileAsync(pathLease).ConfigureAwait(true);
                    RunOnUi(() => tracked.UpdateCounts(current ?? string.Empty));
                }
            }
        }
        catch (Exception)
        {
            // Change tracking is presentation only; it must never break the transcript.
        }
    }

    // Snapshot the pre-edit content on the agent's first write to a path, so Reject can restore it.
    private async Task<ChangedFileViewModel> TrackChangeBeforeWriteAsync(string requestedPath)
    {
        using var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, requestedPath);
        lock (_changedFilesByPath)
        {
            if (_changedFilesByPath.TryGetValue(pathLease.FullPath, out var existing)) return existing;
        }

        var original = await ReadLeasedFileAsync(pathLease).ConfigureAwait(true);
        var entry = new ChangedFileViewModel(pathLease.FullPath, original,
            file => OnUiAsync(() => AcceptChangeAsync(file)),
            file => OnUiAsync(() => RejectChangeAsync(file)));
        lock (_changedFilesByPath)
        {
            if (_changedFilesByPath.TryGetValue(pathLease.FullPath, out var raced)) return raced;
            _changedFilesByPath[pathLease.FullPath] = entry;
        }

        RunOnUi(() => ChangedFiles.Add(entry));
        return entry;
    }

    private async Task<string?> ReadLeasedFileAsync(WorkspacePathLease pathLease)
    {
        using (var document = pathLease.ProtectDocument())
        {
            if (document is not null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var liveText = await _services.TryReadOpenDocumentAsync(pathLease.FullPath, _lifetime.Token).ConfigureAwait(true);
                if (liveText is not null) return liveText;
            }
        }

        try { return pathLease.ReadAllText(); }
        catch (FileNotFoundException) { return null; }
    }

    private async Task WriteLeasedFileAsync(WorkspacePathLease pathLease, string content)
    {
        using (var document = pathLease.ProtectDocument())
        {
            if ((document is not null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                && await _services.TryWriteOpenDocumentAsync(pathLease.FullPath, content, _lifetime.Token).ConfigureAwait(true))
            {
                return;
            }
        }

        pathLease.WriteAllText(content);
    }

    private Task AcceptChangeAsync(ChangedFileViewModel file)
    {
        UntrackChange(file);
        return Task.CompletedTask;
    }

    private async Task RejectChangeAsync(ChangedFileViewModel file)
    {
        try
        {
            if (file.OriginalText is null)
            {
                string fullPath;
                using (var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, file.FullPath)) fullPath = pathLease.FullPath;
                File.Delete(fullPath);
            }
            else
            {
                using var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, file.FullPath);
                await WriteLeasedFileAsync(pathLease, file.OriginalText).ConfigureAwait(true);
            }

            UntrackChange(file);
        }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not revert {file.Name}: {ex.Message}";
        }
    }

    private void UntrackChange(ChangedFileViewModel file)
    {
        lock (_changedFilesByPath) _changedFilesByPath.Remove(file.FullPath);
        ChangedFiles.Remove(file);
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
        IsRemoteControlEnabled = false;
        RemoteControlUrl = null;
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
        ToggleRemoteControlCommand.NotifyCanExecuteChanged();
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
        _services.ActiveDocumentChanged -= OnActiveDocumentChanged;
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
