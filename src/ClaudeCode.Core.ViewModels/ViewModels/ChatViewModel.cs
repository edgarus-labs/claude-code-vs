using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
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
    private string? _workspaceRoot;
    private SessionConfigOption? _modelOption;
    private SessionConfigOption? _effortOption;
    private SessionConfigOption? _modeOption;
    private SessionConfigValue? _selectedModel;
    private SessionConfigValue? _selectedEffort;
    private SessionConfigValue? _selectedMode;
    private static readonly string[] AutoEffortValues =
        Enum.GetValues(typeof(EffortLevel)).Cast<EffortLevel>().Select(level => level.ToAgentValue()).ToArray();
    private IAutoEffortServices? AutoServices => _services as IAutoEffortServices;
    private IChatErrorLog? ErrorLog => _services as IChatErrorLog;
    private readonly SessionConfigValue _autoEffort = new SessionConfigValue(
        "auto", "Auto", "Low, Medium or High for each message, judged per message");
    private bool _autoEffortSelected;
    private bool _isAutoEffort;
    private EffortLevel? _lastAutoEffort;
    private CancellationTokenSource? _autoEffortStop;
    private string? _autoEffortSetTo;
    private bool _isJudgingEffort;
    private bool _isPickingEffort;
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
    private bool _isSwitchingSession;
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
    private bool _isAuthCommandRunning;
    private CancellationTokenSource? _authCommandCts;

    private static readonly AvailableCommand LoginCommand =
        new AvailableCommand("login", "Sign in to Claude Code (opens a console and your browser)");
    private static readonly AvailableCommand LogoutCommand =
        new AvailableCommand("logout", "Sign out of Claude Code everywhere on this machine");
    private static readonly IReadOnlyList<AvailableCommand> ClientCommands = new[] { LoginCommand, LogoutCommand };

    private const long MaxImageAttachmentBytes = 5L * 1024 * 1024;
    private const long MaxDocumentAttachmentBytes = 1L * 1024 * 1024;
    private const int UsageWarningThresholdPercent = 75;
    private static readonly TimeSpan UsagePollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UsagePollRetryInterval = TimeSpan.FromSeconds(15);

    private const int UsagePollFastRetries = 4;

    internal static TimeSpan NextUsagePollDelay(int consecutiveFailures) =>
        consecutiveFailures > 0 && consecutiveFailures <= UsagePollFastRetries ? UsagePollRetryInterval : UsagePollInterval;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _emptyElicitationContent =
        new Dictionary<string, IReadOnlyList<string>>();

    public ChatViewModel(IChatSessionServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ChatViewModel must be constructed on a thread with a SynchronizationContext (e.g. the WPF UI thread).");
        SendCommand = new AsyncRelayCommand(SendAsync, CanSend, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsBusy && !_disposed && _connection is not null && _sessionId is not null);
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => !IsSignedIn && !_disposed);
        NewSessionCommand = new AsyncRelayCommand(NewSessionAsync, () => CanEditDraft);
        ShowHistoryCommand = new AsyncRelayCommand(ShowHistoryAsync, () => CanEditDraft);
        OpenSessionCommand = new AsyncRelayCommand<SessionSummary>(OpenSessionAsync, session => CanEditDraft && session is not null);
        AttachActiveDocumentCommand = new AsyncRelayCommand(AttachActiveDocumentAsync, () => CanEditDraft && !_isCapturingDocument && _services.HasActiveDocument);
        _services.ActiveDocumentChanged += OnActiveDocumentChanged;
        _workspaceRoot = TryReadWorkspaceRoot();
        _services.WorkspaceRootChanged += OnWorkspaceRootChanged;
        ApplySlashSuggestionCommand = new RelayCommand<AvailableCommand>(ApplySlashSuggestion,
            command => CanShowSlashPopup && AreSlashSuggestionsVisible && command is not null && SlashSuggestions.Contains(command));
        CancelAuthCommand = new RelayCommand(() => _authCommandCts?.Cancel(), () => IsAuthCommandRunning);
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
        }), () => ChangedFiles.Count > 0);
        RejectAllChangesCommand = new AsyncRelayCommand(() => OnUiAsync(RejectAllChangesAsync), () => ChangedFiles.Count > 0);
        ChangedFiles.CollectionChanged += (_, __) =>
        {
            AcceptAllChangesCommand.NotifyCanExecuteChanged();
            RejectAllChangesCommand.NotifyCanExecuteChanged();
        };
        OpenChangedFileCommand = new AsyncRelayCommand<ChangedFileViewModel>(OpenChangedFileAsync);
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
    public IRelayCommand CloseUsagePanelCommand { get; }
    public IRelayCommand DismissUsageWarningCommand { get; }
    public IRelayCommand CancelAuthCommand { get; }
    public Task Initialization { get; }

    /// <summary>True while a client-side /login or /logout console command is running.</summary>
    public bool IsAuthCommandRunning
    {
        get => _isAuthCommandRunning;
        private set
        {
            if (SetProperty(ref _isAuthCommandRunning, value))
            {
                CancelAuthCommand.NotifyCanExecuteChanged();
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

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
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            if (sessionId != _sessionId)
            {
                if (state.Enabled) _ = TryDisableRemoteControlAsync(connection, sessionId);
                return;
            }
            IsRemoteControlEnabled = state.Enabled;
            RemoteControlUrl = state.Enabled ? state.SessionUrl : null;
            StatusMessage = null;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(connection, _connection) && sessionId == _sessionId) StatusMessage = $"Remote Control: {ex.Message}";
        }
        finally
        {
            IsRemoteControlBusy = false;
            if (!_disposed && _services.RemoteControlAtStartup && _sessionId is not null
                && (sessionId != _sessionId || !ReferenceEquals(connection, _connection)))
            {
                _ = SetRemoteControlAsync(true);
            }
        }
    }

    private async Task<bool> TryDisableRemoteControlAsync(IAcpAgentConnection connection, string sessionId)
    {
        try
        {
            var state = await connection.SetRemoteControlAsync(sessionId, false, null, _lifetime.Token).ConfigureAwait(true);
            return !state.Enabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task LeaveRemoteControlAsync(IAcpAgentConnection connection, string sessionId)
    {
        if (!IsRemoteControlEnabled) return;
        if (await TryDisableRemoteControlAsync(connection, sessionId).ConfigureAwait(true) && !_disposed && sessionId == _sessionId)
        {
            IsRemoteControlEnabled = false;
            RemoteControlUrl = null;
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

    private void OnSessionStarted()
    {
        ForgetAutoVerdict();
        NotifySelectionsChanged();
        IsRemoteControlEnabled = false;
        RemoteControlUrl = null;
        if (_services.RemoteControlAtStartup) _ = SetRemoteControlAsync(true);
    }

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

    private bool CanEditDraft => CanQueueOrSendDraft && !IsBusy;

    private bool CanShowSlashPopup => !_disposed && !IsBusy && !_isSwitchingSession;

    private bool CanQueueOrSendDraft => !_disposed && !NeedsAuthentication && !IsConnecting
        && !IsConfigBusy && !_isSwitchingSession;

    private string WorkspaceCwd => _services.WorkspaceRoot ?? Environment.CurrentDirectory;

    public bool CanConfigure => !_disposed && !NeedsAuthentication && !IsConnecting && !IsConfigBusy && !_isJudgingEffort &&
        !_isCapturingDocument && !_isSwitchingSession && _sessionId is not null;
    public bool HasEffort => AvailableEfforts.Count > 0;
    public bool HasModes => AvailableModes.Count > 0;
    public string ActiveModelName => _selectedModel?.Name ?? "Model unavailable";
    public string ActiveEffortName => !_isAutoEffort ? _selectedEffort?.Name ?? string.Empty
        : _selectedEffort is { } inEffect && inEffect.Value == _autoEffortSetTo ? _autoEffort.Name + " · " + inEffect.Name
        : _autoEffort.Name;
    public string ModelEffortLabel => HasEffort && ActiveEffortName.Length > 0 ? ActiveModelName + " · " + ActiveEffortName : ActiveModelName;
    public string ActiveModeName => _selectedMode?.Name ?? "Mode unavailable";

    public SessionConfigValue? SelectedModel
    {
        get => _selectedModel;
        set => _ = SelectModelAsync(value);
    }

    public SessionConfigValue? SelectedEffort
    {
        get => _isAutoEffort ? _autoEffort : _selectedEffort;
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
        if (_explicitSessionTitle is null && FirstUserMessageTitle() is { } title) SessionTitle = title;
    }

    private string? FirstUserMessageTitle() =>
        Messages.Count > 0 && Messages[0].Role == ChatRole.User &&
        SessionTitleFormat.Describe(Messages[0].Text, sessionId: null) is { Length: > 0 } title ? title : null;

    private static string? NormalizeSessionTitle(string? title) =>
        SessionTitleFormat.Describe(title, sessionId: null) is { Length: > 0 } normalized ? normalized : null;

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
        int consecutiveFailures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                UsageSnapshot? snapshot = await _services.UsageService.GetUsageAsync(_lifetime.Token).ConfigureAwait(false);
                consecutiveFailures = snapshot is null ? consecutiveFailures + 1 : 0;
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
                consecutiveFailures++;
            }

            TimeSpan delay = NextUsagePollDelay(consecutiveFailures);
            try
            {
                await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
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
            ? $"Resets {local:t}"
            : $"Resets {local:dddd} {local:t}";
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

    /// <summary>Raw usage snapshot; null until the first usage fetch succeeds.</summary>
    public UsageSnapshot? Usage
    {
        get => _usage;
        private set
        {
            if (SetProperty(ref _usage, value))
            {
                OnPropertyChanged(nameof(SessionUsage));
                OnPropertyChanged(nameof(WeeklyUsage));
                OnPropertyChanged(nameof(ScopedWeeklyUsage));
            }
        }
    }

    public UsageLimitDisplay? SessionUsage => BuildUsageDisplay("Current session", limit => limit.Kind == "session");

    public UsageLimitDisplay? WeeklyUsage => BuildUsageDisplay("This week", limit => limit.Kind == "weekly_all");

    /// <summary>The agent's <c>weekly_scoped</c> limit, labelled with the scope the agent named.</summary>
    public UsageLimitDisplay? ScopedWeeklyUsage => BuildUsageDisplay(
        (_usage?.Limits.FirstOrDefault(limit => limit.Kind == "weekly_scoped")?.ScopeLabel ?? "Scoped") + " this week",
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

    private void OnWorkspaceRootChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_disposed) return;
        var root = TryReadWorkspaceRoot();
        if (root is null || string.Equals(root, _workspaceRoot, StringComparison.OrdinalIgnoreCase)) return;
        _workspaceRoot = root;
        _ = SwitchWorkspaceAsync();
    });

    private async Task SwitchWorkspaceAsync()
    {
        _isSwitchingSession = true;
        StatusMessage = null;
        NotifyStateChanged();
        try
        {
            await ReleaseConnectionAsync().ConfigureAwait(true);
            if (_disposed) return;
            ResetTranscriptState();
            await InitializeCoreAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed || _lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _workspaceRoot = null;
            if (!_disposed) StatusMessage = $"Could not switch to the new workspace: {ex.Message}";
        }
        finally
        {
            _isSwitchingSession = false;
            NotifyStateChanged();
        }
    }

    private string? TryReadWorkspaceRoot()
    {
        try { return _services.WorkspaceRoot; }
        catch { return null; }
    }

    public bool HasActiveDocument => _services.HasActiveDocument;

    public void DismissAttachmentError() => RunOnUi(() => AttachmentError = null);

    private Task AttachActiveDocumentAsync(CancellationToken cancellationToken) =>
        OnUiAsync(() => RunReportingFailuresAsync(() => AttachActiveDocumentCoreAsync(cancellationToken)));

    private async Task AttachActiveDocumentCoreAsync(CancellationToken cancellationToken)
    {
        if (!CanEditDraft || _isCapturingDocument) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _isCapturingDocument = true;
        try
        {
            NotifyStateChanged();
            AttachmentError = null;
            await AttachCapturedDocumentAsync(linked.Token).ConfigureAwait(true);
        }
        finally
        {
            _isCapturingDocument = false;
            RunEachStepReportingFailures(NotifyStateChanged, SendPendingPlanReview);
        }
    }

    private async Task AttachCapturedDocumentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await _services.CaptureActiveDocumentAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFailure("Attaching the active document failed.", ex);
            if (!_disposed) AttachmentError = $"Could not attach the active document: {ex.Message}";
        }
    }

    public void DismissSlashSuggestions() => RunOnUi(() =>
    {
        _slashSuggestionsDismissed = true;
        UpdateSlashPresentation();
    });

    private void ApplySlashSuggestion(AvailableCommand? command)
    {
        if (!CanShowSlashPopup || !AreSlashSuggestionsVisible || command is null || !SlashSuggestions.Contains(command)) return;
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
            foreach (var command in _availableCommands.Concat(ClientCommands))
                if (command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) SlashSuggestions.Add(command);
        }
        SelectedSlashSuggestion = SlashSuggestions.FirstOrDefault(command => command.Name == selectedName)
            ?? SlashSuggestions.FirstOrDefault();
        UpdateSlashPresentation();
    }

    private void UpdateSlashPresentation()
    {
        var isSlashToken = IsSlashToken;
        AreSlashSuggestionsVisible = CanShowSlashPopup && isSlashToken && !_slashSuggestionsDismissed;
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

    public Task SelectModelAsync(SessionConfigValue? value) => OnUiAsync(() => RunReportingFailuresAsync(() => ChangeConfigAsync(_modelOption, value)));
    public Task SelectEffortAsync(SessionConfigValue? value) => OnUiAsync(() => RunReportingFailuresAsync(() => SelectEffortCoreAsync(value)));
    public Task SelectModeAsync(SessionConfigValue? value) => OnUiAsync(() => RunReportingFailuresAsync(() => ChangeConfigAsync(_modeOption, value)));

    private async Task SelectEffortCoreAsync(SessionConfigValue? value)
    {
        if (!CanConfigure || value is null || !AvailableEfforts.Contains(value))
        {
            await ChangeConfigAsync(_effortOption, value).ConfigureAwait(true);
            return;
        }
        if (ReferenceEquals(value, _autoEffort))
        {
            _autoEffortSelected = _isAutoEffort = true;
            ForgetAutoVerdict();
            NotifySelectionsChanged();
            return;
        }
        _isPickingEffort = true;
        try
        {
            await ChangeConfigAsync(_effortOption, value).ConfigureAwait(true);
        }
        finally
        {
            _isPickingEffort = false;
        }
        RunEachStepReportingFailures(
            () =>
            {
                if (_autoEffortSelected && _effortOption?.CurrentValue == value.Value)
                {
                    _autoEffortSelected = _isAutoEffort = false;
                    NotifySelectionsChanged();
                }
            },
            DispatchNextQueuedMessage);
    }

    private void ForgetAutoVerdict()
    {
        _lastAutoEffort = null;
        _autoEffortSetTo = null;
    }

    private async Task<bool> ApplyAutoEffortAsync(IAcpAgentConnection connection, string sessionId, string prompt)
    {
        var classifier = AutoServices?.EffortClassifier;
        if (classifier is null) return true;
        _isJudgingEffort = true;
        NotifyStateChanged();
        var previousActivity = ActivityText;
        ActivityText = "Judging effort…";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _autoEffortStop = stop;
        try
        {
            EffortLevel? judged = null;
            string? failure = null;
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                try
                {
                    judged = await classifier.ClassifyAsync(prompt, stop.Token).ConfigureAwait(true);
                    if (judged is { } verdict && !Enum.IsDefined(typeof(EffortLevel), verdict))
                        throw new System.IO.InvalidDataException("The effort judge returned an unknown level (" + (int)verdict + ").");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return false; }
                catch (Exception ex)
                {
                    judged = null;
                    failure = Describe(ex);
                    LogFailure("Auto effort could not judge a message.", ex);
                }
                if (stop.IsCancellationRequested) return false;
            }

            if (!IsCurrentSession(connection, sessionId)) return false;
            EffortLevel level = judged ?? _lastAutoEffort ?? EffortLevel.High;
            if (judged is not null) _lastAutoEffort = level;
            if (failure is not null) StatusMessage = $"Auto effort could not judge this message, so it runs at {level}: {failure}";
            var option = _effortOption;
            if (option is null) return true;
            var value = level.ToAgentValue();
            if (!option.Options.Any(candidate => candidate.Value == value)) return true;
            if (option.CurrentValue != value)
            {
                IReadOnlyList<SessionConfigOption> options;
                try
                {
                    options = await connection.SetSessionConfigOptionAsync(sessionId, option.Id, value, _lifetime.Token).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"Auto effort could not set the effort to {value}: {Describe(ex)}", ex);
                }
                if (!IsCurrentSession(connection, sessionId)) return false;
                ApplyConfigOptions(options);
            }
            _autoEffortSetTo = value;
            return !stop.IsCancellationRequested;
        }
        finally
        {
            _autoEffortStop = null;
            if (!_disposed) ActivityText = previousActivity;
            _isJudgingEffort = false;
            NotifyStateChanged();
            NotifySelectionsChanged();
        }
    }

    private static string Describe(Exception exception) =>
        string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message;

    private bool IsCurrentSession(IAcpAgentConnection connection, string sessionId) =>
        !_disposed && ReferenceEquals(connection, _connection) && sessionId == _sessionId;

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
        try
        {
            IsConfigBusy = true;
            StatusMessage = null;
            var options = await connection.SetSessionConfigOptionAsync(sessionId, option.Id, value.Value, _lifetime.Token).ConfigureAwait(true);
            if (IsCurrentSession(connection, sessionId))
                ApplyConfigOptions(options);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            LogFailure("Changing a session setting failed.", ex);
            if (!_disposed) StatusMessage = $"Could not change session settings: {ex.Message}";
        }
        finally
        {
            RunEachStepReportingFailures(
                () => IsConfigBusy = false,
                NotifySelectionsChanged,
                SendPendingPlanReview,
                () => { if (!_isPickingEffort) DispatchNextQueuedMessage(); });
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
            LogFailure("Preparing Claude failed.", ex);
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

    private bool CanSend() => !_isCapturingDocument && (!string.IsNullOrWhiteSpace(InputText) || Attachments.Count > 0) &&
        (TryGetClientCommand(InputText.Trim(), out _) ? CanRunClientCommand : CanQueueOrSendDraft);

    private bool CanRunClientCommand => CanShowSlashPopup && !IsAuthCommandRunning;

    private enum ClientSlashCommand { Login, Logout }

    private static bool TryGetClientCommand(string trimmedInput, out ClientSlashCommand command)
    {
        var token = trimmedInput;
        int spaceIndex = token.IndexOf(' ');
        if (spaceIndex >= 0) token = token.Substring(0, spaceIndex);
        if (string.Equals(token, "/" + LoginCommand.Name, StringComparison.OrdinalIgnoreCase)) { command = ClientSlashCommand.Login; return true; }
        if (string.Equals(token, "/" + LogoutCommand.Name, StringComparison.OrdinalIgnoreCase)) { command = ClientSlashCommand.Logout; return true; }
        command = default;
        return false;
    }

    public Task SendAsync() => OnUiAsync(() => SendCoreAsync(userSent: true));

    private sealed class QueuedMessage
    {
        public QueuedMessage(ChatMessageViewModel bubble, string text, IReadOnlyList<ChatAttachmentViewModel> attachments)
        {
            Bubble = bubble;
            Text = text;
            Attachments = attachments;
        }

        public ChatMessageViewModel Bubble { get; }
        public string Text { get; }
        public IReadOnlyList<ChatAttachmentViewModel> Attachments { get; }
    }

    private readonly Queue<QueuedMessage> _queuedMessages = new Queue<QueuedMessage>();

    private readonly List<QueuedMessage?> _submittedPrompts = new List<QueuedMessage?>();

    private int _runningTurns;

    private bool _isStopping;

    private bool _runningFailed;

    private readonly List<QueuedMessage> _returnedUnstarted = new List<QueuedMessage>();

    private QueuedMessage? _draftInFlight;

    private Task SendCoreAsync(bool userSent)
    {
        if (!CanSend()) return Task.CompletedTask;

        var text = InputText.Trim();
        if (TryGetClientCommand(text, out var clientCommand))
        {
            return RunReportingFailuresAsync(() => RunClientCommandAsync(clientCommand));
        }

        var attachments = Attachments.ToArray();
        if (userSent)
        {
            try { StatusMessage = null; }
            catch (Exception ex) { ReportUnexpectedFailure(ex); }
        }

        if (IsBusy)
        {
            try { EnqueueDraft(text, attachments); }
            catch (Exception ex) { ReportUnexpectedFailure(ex); }
            return Task.CompletedTask;
        }

        var bubble = BuildUserBubble(text, attachments, isPending: true);
        var draft = new QueuedMessage(bubble, text, attachments);
        _draftInFlight = draft;
        return RunTurnReportingFailuresAsync(null, text,
            accept: () =>
            {
                ConsumeDraft(text, attachments);
                var firstQueued = Messages.FirstOrDefault(message => message.Role == ChatRole.User && message.IsPending);
                if (firstQueued is null) Messages.Add(bubble);
                else Messages.Insert(Messages.IndexOf(firstQueued), bubble);
                UpdateSessionTitleFromFirstUserMessage();
            },
            prepare: () =>
            {
                bubble.MarkSent();
                _draftInFlight = null;
                return (text, draft.Attachments);
            });
    }

    private async Task RunClientCommandAsync(ClientSlashCommand command)
    {
        if (!CanRunClientCommand) return;
        bool login = command == ClientSlashCommand.Login;
        InputText = string.Empty;
        StatusMessage = null;

        _authCommandCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _authCommandCts.Token;
        bool signedIn = false;
        try
        {
            IsAuthCommandRunning = true;
            AuthCommandOutcome outcome;
            if (login)
            {
                StatusMessage = "Opening a console to sign in to Claude Code…";
                var progress = new Progress<string>(message => RunOnUi(() => { if (!_disposed) StatusMessage = message; }));
                outcome = await _services.AuthService.LaunchInteractiveLoginAsync(token, progress).ConfigureAwait(true);
            }
            else
            {
                if (!await _services.ConfirmSignOutEverywhereAsync(token).ConfigureAwait(true)) return;
                StatusMessage = "Signing out of Claude Code…";
                outcome = await _services.AuthService.LaunchInteractiveLogoutAsync(token).ConfigureAwait(true);
            }

            if (_disposed) return;
            StatusMessage = outcome.Message;
            signedIn = login && outcome.Succeeded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_disposed) StatusMessage = login ? "Sign-in cancelled." : "Sign-out cancelled.";
        }
        catch (Exception ex)
        {
            LogFailure(login ? "Signing in failed." : "Signing out failed.", ex);
            if (!_disposed) StatusMessage = $"{(login ? "Sign-in" : "Sign-out")} failed: {ex.Message}";
        }
        finally
        {
            _authCommandCts?.Dispose();
            _authCommandCts = null;
            RunEachStepReportingFailures(() => IsAuthCommandRunning = false);
        }

        if (signedIn && !_disposed) await InitializeCoreAsync(_lifetime.Token).ConfigureAwait(true);
    }

    private void EnqueueDraft(string text, ChatAttachmentViewModel[] attachments)
    {
        var bubble = BuildUserBubble(text, attachments, isPending: true);
        _queuedMessages.Enqueue(new QueuedMessage(bubble, text, attachments));
        RunEachStep(new Action[]
        {
            () => ConsumeDraft(text, attachments),
            () => Messages.Add(bubble),
            UpdateSessionTitleFromFirstUserMessage,
        });
        DispatchNextQueuedMessage();
    }

    private static ChatMessageViewModel BuildUserBubble(string text, ChatAttachmentViewModel[] attachments, bool isPending)
    {
        var transcriptText = string.Join(Environment.NewLine, new[] { text }
            .Where(part => part.Length > 0).Concat(attachments.Where(attachment => attachment.IsDocument)
                .Select(attachment => "[Document: " + attachment.Name + "]")));
        return new ChatMessageViewModel(ChatRole.User, transcriptText, isPending)
        {
            Images = attachments.Where(attachment => attachment.IsImage)
                .Select(attachment => new ChatMessageImage(attachment.Name, attachment.MimeType, attachment.Base64Data)).ToList(),
        };
    }

    private void ConsumeDraft(string text, ChatAttachmentViewModel[] attachments)
    {
        if (InputText.Trim() == text) InputText = string.Empty;
        foreach (var attachment in attachments) Attachments.Remove(attachment);
        AttachmentError = null;
    }

    private Task DispatchQueuedMessageAsync(QueuedMessage queued) =>
        RunTurnReportingFailuresAsync(queued, queued.Text, () => (queued.Text, queued.Attachments));

    private async Task RunTurnReportingFailuresAsync(QueuedMessage? queued, string promptText,
        Func<(string Text, IReadOnlyList<ChatAttachmentViewModel> Attachments)> prepare, Action? accept = null)
    {
        try
        {
            await RunTurnAsync(queued, promptText, prepare, accept).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportUnexpectedFailure(ex);
        }

        if (_runningTurns > 0) return;
        try
        {
            SendPendingPlanReview();
            DispatchNextQueuedMessage();
        }
        catch (Exception ex)
        {
            ReportUnexpectedFailure(ex);
        }
    }

    private async Task RunTurnAsync(QueuedMessage? queued, string promptText,
        Func<(string Text, IReadOnlyList<ChatAttachmentViewModel> Attachments)> prepare, Action? accept = null)
    {
        bool joining = _runningTurns++ > 0;
        bool submitted = false;
        bool failed = false;
        bool abandoned = false;
        bool sessionLost = false;
        string? stopReason = null;
        IAcpAgentConnection? turnConnection = null;
        string? turnSessionId = null;
        try
        {
            if (!joining)
            {
                ActivityText = "Working…";
                IsBusy = true;
                _turnStartedAt = DateTimeOffset.UtcNow;
                _turnStartUsedTokens = _sessionUsedTokens;
                TurnTokens = null;
            }
            accept?.Invoke();
            var (connection, sessionId) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            (turnConnection, turnSessionId) = (connection, sessionId);
            if (_disposed) return;
            if (_isAutoEffort && !joining)
            {
                var ready = await ApplyAutoEffortAsync(connection, sessionId, promptText).ConfigureAwait(true);
                if (_disposed) return;
                sessionLost = !IsCurrentSession(connection, sessionId);
                if (sessionLost && queued is null)
                    AppendStatus("The session changed while judging effort, so your message was not sent.");
                abandoned = !ready || sessionLost;
            }
            if (!abandoned)
            {
                var (text, attachments) = prepare();
                var content = new List<ContentBlock>(attachments.Count + 1);
                if (text.Length > 0) content.Add(new ContentBlock.Text(text));
                foreach (var attachment in attachments)
                    content.Add(attachment.ToContentBlock());

                if (!joining) _currentAssistantMessage = null;
                _submittedPrompts.Add(queued);
                submitted = true;
                if (_submittedPrompts.Count == 1 && queued is not null) RunEachStepReportingFailures(queued.Bubble.MarkSent);
                stopReason = await connection.SendPromptAsync(sessionId, content, _lifetime.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            failed = true;
            bool releasedUnderIt = turnConnection is not null && !IsCurrentSession(turnConnection, turnSessionId!);
            if (!releasedUnderIt)
            {
                LogFailure("A turn failed.", ex);
                if (!_disposed) StatusMessage = $"Error: {ex.Message}";
            }
        }
        finally
        {
            try
            {
                if (queued is null)
                {
                    var returning = new List<QueuedMessage>();
                    var left = _draftInFlight;
                    if (left is not null) returning.Add(left);
                    _draftInFlight = null;
                    int behind = 0;
                    if (failed && !submitted && !_disposed)
                    {
                        behind = _queuedMessages.Count;
                        returning.AddRange(_queuedMessages);
                        _queuedMessages.Clear();
                    }
                    if (!_disposed)
                    {
                        bool composerHeldText = InputText.Trim().Length > 0;
                        ReturnToComposer(returning);
                        if (left is not null && composerHeldText)
                            AppendStatus("Your message is back in the message box, together with what was already there.");
                        AppendRestoreNotice(behind,
                            "Your queued message was not sent because the message before it failed - it is back in the message box, after that one.",
                            "{0} queued messages were not sent because the message before them failed - they are back in the message box, after it.");
                    }
                }
                if (failed) _runningFailed |= !submitted || queued is null || !queued.Bubble.IsPending;
                if (submitted) OnPromptReturned(queued, stopReason);
                else if (queued is not null && !_disposed)
                {
                    if (sessionLost)
                    {
                        Messages.Remove(queued.Bubble);
                        StatusMessage = WithQueueNotice(StatusMessage, 1);
                    }
                    else _returnedUnstarted.Add(queued);
                }
            }
            finally
            {
                if (--_runningTurns == 0)
                {
                    IsBusy = false;
                    ActivityText = string.Empty;
                    _currentAssistantMessage = null;
                    if (_isStopping || failed) ClearRunningSubagents();
                    _isStopping = false;
                    bool runningFailed = _runningFailed;
                    _runningFailed = false;
                    if (runningFailed && _returnedUnstarted.Count > 0)
                    {
                        var toComposer = _returnedUnstarted.Concat(_queuedMessages).ToList();
                        _returnedUnstarted.Clear();
                        _queuedMessages.Clear();
                        RestoreToComposer(toComposer,
                            "Your queued message was not delivered - it is back in the message box.",
                            "{0} queued messages were not delivered - they are back in the message box.");
                    }
                    RequeueReturnedUnstarted();
                }
            }
        }
    }

    private void OnPromptReturned(QueuedMessage? queued, string? stopReason)
    {
        int index = _submittedPrompts.IndexOf(queued);
        if (index < 0) return;
        _submittedPrompts.RemoveAt(index);
        bool endedNormally = stopReason is not null and not "cancelled";
        if (queued is not null && queued.Bubble.IsPending)
        {
            if (!endedNormally)
            {
                _returnedUnstarted.Add(queued);
                return;
            }
            queued.Bubble.MarkSent();
        }
        if (index != 0 || _submittedPrompts.Count == 0 || !endedNormally) return;
        _currentAssistantMessage = null;
        _submittedPrompts[0]?.Bubble.MarkSent();
    }

    private bool CanSendAhead => !_isStopping && _returnedUnstarted.Count == 0 && !_isAutoEffort && _connection?.SupportsPromptQueueing == true;

    private void DispatchNextQueuedMessage()
    {
        while (_queuedMessages.Count > 0 && CanQueueOrSendDraft && (!IsBusy || CanSendAhead))
            _ = DispatchQueuedMessageAsync(_queuedMessages.Dequeue());
    }

    private void RestoreToComposer(List<QueuedMessage> pending, string one, string many)
    {
        if (pending.Count == 0) return;
        var restored = pending.ToList();
        pending.Clear();
        ReturnToComposer(restored);
        AppendRestoreNotice(restored.Count, one, many);
    }

    private void ReturnToComposer(IReadOnlyList<QueuedMessage> messages)
    {
        if (messages.Count == 0) return;
        var texts = messages.Select(message => message.Text).Where(text => text.Length > 0).ToList();
        if (InputText.Trim().Length > 0) texts.Add(InputText);
        var steps = new List<Action> { () => InputText = string.Join(Environment.NewLine + Environment.NewLine, texts) };
        foreach (var attachment in messages.SelectMany(message => message.Attachments))
            steps.Add(() => { if (!Attachments.Contains(attachment)) Attachments.Add(attachment); });
        foreach (var message in messages) steps.Add(() => Messages.Remove(message.Bubble));
        steps.Add(RefreshSessionTitleAfterRemoval);
        RunEachStep(steps);
    }

    private void AppendRestoreNotice(int count, string one, string many)
    {
        if (count == 0) return;
        AppendStatus(count == 1 ? one : string.Format(System.Globalization.CultureInfo.InvariantCulture, many, count));
    }

    private void ReportUnexpectedFailure(Exception ex)
    {
        if (_disposed && ex is OperationCanceledException) return;
        LogFailure("The chat panel failed in its own bookkeeping.", ex);
        if (_disposed) return;
        try { AppendStatus($"Error: {ex.Message}"); }
        catch (Exception statusFailure) { LogFailure("Showing a failure in the status line failed.", statusFailure); }
    }

    private async Task RunReportingFailuresAsync(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(true); }
        catch (Exception ex) { ReportUnexpectedFailure(ex); }
    }

    private void RunEachStepReportingFailures(params Action[] steps)
    {
        try { RunEachStep(steps); }
        catch (Exception ex) { ReportUnexpectedFailure(ex); }
    }

    private void LogFailure(string message, Exception ex)
    {
        try { ErrorLog?.LogError(message, ex); }
        catch (Exception) { }
    }

    private void RunEachStep(IEnumerable<Action> steps)
    {
        ExceptionDispatchInfo? first = null;
        foreach (var step in steps)
        {
            try { step(); }
            catch (Exception ex)
            {
                if (first is null) first = ExceptionDispatchInfo.Capture(ex);
                else LogFailure("A later step failed too.", ex);
            }
        }
        first?.Throw();
    }

    private void AppendStatus(string text) =>
        StatusMessage = string.IsNullOrEmpty(StatusMessage) ? text : StatusMessage + " " + text;

    private void RefreshSessionTitleAfterRemoval()
    {
        if (_explicitSessionTitle is null) SessionTitle = FirstUserMessageTitle() ?? UntitledSessionTitle;
    }

    private bool RequeueReturnedUnstarted()
    {
        if (_returnedUnstarted.Count == 0) return false;
        var ordered = _returnedUnstarted.Concat(_queuedMessages).ToList();
        _returnedUnstarted.Clear();
        _queuedMessages.Clear();
        foreach (var message in ordered) _queuedMessages.Enqueue(message);
        return true;
    }

    private int DiscardQueuedMessages()
    {
        var dropped = _submittedPrompts.Skip(1).Select(prompt => prompt!)
            .Concat(_returnedUnstarted).Concat(_queuedMessages).ToList();
        if (_submittedPrompts.Count > 1) _submittedPrompts.RemoveRange(1, _submittedPrompts.Count - 1);
        _returnedUnstarted.Clear();
        _queuedMessages.Clear();
        foreach (var message in dropped) Messages.Remove(message.Bubble);
        return dropped.Count;
    }

    private static string? WithQueueNotice(string? status, int discarded)
    {
        if (discarded <= 0) return status;
        var notice = discarded == 1
            ? "A queued message was not sent."
            : discarded + " queued messages were not sent.";
        return string.IsNullOrEmpty(status) ? notice : status + " " + notice;
    }

    private string? _pendingPlanReviewComments;
    private PlanReviewViewModel? _pendingPlan;

    /// <summary>The implementation plan currently awaiting Proceed/Review, kept (resolved) until the
    /// next plan or session change.</summary>
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
        AttentionRequested?.Invoke(this, new ChatAttentionEventArgs(kind, title, SessionTitleFormat.SingleLine(message, 160)));

    private void SendPendingPlanReview()
    {
        var comments = _pendingPlanReviewComments;
        if (comments is null || _disposed || !CanEditDraft || _isCapturingDocument) return;
        _pendingPlanReviewComments = null;
        var draft = InputText;
        var draftAttachments = Attachments.ToArray();
        try
        {
            InputText = "Review comments on the plan:\n" + comments;
            Attachments.Clear();
            _ = SendCoreAsync(userSent: false);
        }
        catch (Exception ex)
        {
            ReportUnexpectedFailure(ex);
        }
        finally
        {
            try
            {
                if (!_disposed)
                {
                    var steps = new List<Action>();
                    if (draft.Trim().Length > 0)
                        steps.Add(() => InputText = InputText.Trim().Length == 0 ? draft : InputText + Environment.NewLine + Environment.NewLine + draft);
                    foreach (var attachment in draftAttachments)
                        steps.Add(() => { if (!Attachments.Contains(attachment)) Attachments.Add(attachment); });
                    RunEachStep(steps);
                }
            }
            catch (Exception ex)
            {
                ReportUnexpectedFailure(ex);
            }
        }
    }

    public Task CancelAsync() => OnUiAsync(CancelCoreAsync);

    private async Task CancelCoreAsync()
    {
        if (_disposed || _connection is null || _sessionId is null) return;
        if (IsBusy) _isStopping = true;
        var judging = _autoEffortStop;
        judging?.Cancel();
        if (judging is not null) return;
        try
        {
            await _connection.CancelAsync(_sessionId, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            _isStopping = false;
            LogFailure("Cancelling the turn failed.", ex);
            if (!_disposed) StatusMessage = $"Cancel failed: {ex.Message}";
            DispatchNextQueuedMessage();
        }
    }

    public Task NewSessionAsync() => OnUiAsync(NewSessionCoreAsync);

    private async Task NewSessionCoreAsync()
    {
        if (!CanEditDraft) return;
        _isSwitchingSession = true;
        StatusMessage = "Starting a new chat…";
        NotifyStateChanged();
        try
        {
            var sessionBeforeConnect = _sessionId;
            var (connection, sessionId) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            if (!string.Equals(sessionId, sessionBeforeConnect, StringComparison.Ordinal))
            {
                var adoptedCommands = _availableCommands;
                var adoptedCatalog = _hasCommandCatalog;
                ResetTranscriptState();
                if (adoptedCatalog) ApplyCommandCatalog(adoptedCommands);
                StatusMessage = null;
                return;
            }

            await LeaveRemoteControlAsync(connection, sessionId).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            var session = await RequestNewSessionAsync(connection, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            ResetTranscriptState();
            AdoptNewSession(session);
            StatusMessage = null;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not start a new session: {ex.Message}";
        }
        finally
        {
            _pendingCommandCatalogs = null;
            _isSwitchingSession = false;
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
            var sessions = await connection.ListSessionsAsync(WorkspaceCwd, _lifetime.Token).ConfigureAwait(true);
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
        StatusMessage = "Opening the chat…";
        var sessionIdBeforeLoad = _sessionId;
        var messagesBeforeLoad = Messages.ToList();
        var changedFilesBeforeLoad = ChangedFiles.ToList();
        var explicitTitleBeforeLoad = _explicitSessionTitle;
        var titleBeforeLoad = SessionTitle;
        var commandsBeforeLoad = _availableCommands;
        var hadCatalogBeforeLoad = _hasCommandCatalog;
        var usedTokensBeforeLoad = _sessionUsedTokens;
        var contextWindowBeforeLoad = _contextWindowSize;
        var turnStartTokensBeforeLoad = _turnStartUsedTokens;
        var turnTokensBeforeLoad = TurnTokens;
        var toolCallLocationsBeforeLoad = _toolCallLocations.ToArray();
        _isSwitchingSession = true;
        NotifyStateChanged();
        try
        {
            var (connection, sessionId) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            await LeaveRemoteControlAsync(connection, sessionId).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            ResetTranscriptState();
            _explicitSessionTitle = NormalizeSessionTitle(session.Title);
            if (_explicitSessionTitle is not null) SessionTitle = _explicitSessionTitle;
            _sessionId = session.SessionId;
            var result = await connection.LoadSessionAsync(session.SessionId, WorkspaceCwd, null, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            ApplyConfigOptions(result.ConfigOptions);
            StatusMessage = null;
            OnSessionStarted();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (_sessionId == session.SessionId)
            {
                _sessionId = sessionIdBeforeLoad;
                ResetTranscriptState();
                foreach (var message in messagesBeforeLoad) Messages.Add(message);
                lock (_changedFilesByPath)
                {
                    foreach (var file in changedFilesBeforeLoad) _changedFilesByPath[file.FullPath] = file;
                }

                foreach (var file in changedFilesBeforeLoad) ChangedFiles.Add(file);
                _toolCallLocations.UnionWith(toolCallLocationsBeforeLoad);
                _explicitSessionTitle = explicitTitleBeforeLoad;
                SessionTitle = titleBeforeLoad;
                if (hadCatalogBeforeLoad) ApplyCommandCatalog(commandsBeforeLoad);
                _sessionUsedTokens = usedTokensBeforeLoad;
                _contextWindowSize = contextWindowBeforeLoad;
                _turnStartUsedTokens = turnStartTokensBeforeLoad;
                TurnTokens = turnTokensBeforeLoad;
                OnPropertyChanged(nameof(SessionUsedTokens));
                OnPropertyChanged(nameof(ContextWindowSize));
                OnPropertyChanged(nameof(ContextUsagePercent));
                OnPropertyChanged(nameof(ContextUsageLabel));
            }

            if (!_disposed) StatusMessage = $"Could not open session: {ex.Message}";
        }
        finally
        {
            _isSwitchingSession = false;
            NotifyStateChanged();
        }
    }

    private void ResetTranscriptState()
    {
        DiscardQueuedMessages();
        Messages.Clear();
        lock (_changedFilesByPath) _changedFilesByPath.Clear();
        _toolCallDiffsById.Clear();
        _toolCallLocations.Clear();
        ClearRunningSubagents();
        ChangedFiles.Clear();
        ClearPendingRequests("The session was replaced.");
        _explicitSessionTitle = null;
        SessionTitle = UntitledSessionTitle;
        _currentAssistantMessage = null;
        _currentUserMessage = null;
        CurrentPlan = null;
        _availableCommands = Array.Empty<AvailableCommand>();
        _hasCommandCatalog = false;
        RefreshSlashSuggestions();
        ActivityText = string.Empty;
        _sessionUsedTokens = 0;
        _contextWindowSize = null;
        _turnStartUsedTokens = 0;
        TurnTokens = null;
        OnPropertyChanged(nameof(SessionUsedTokens));
        OnPropertyChanged(nameof(ContextWindowSize));
        OnPropertyChanged(nameof(ContextUsagePercent));
        OnPropertyChanged(nameof(ContextUsageLabel));
    }

    private void ClearPendingRequests(string reason)
    {
        _pendingPlan?.MarkResolved("Session ended");
        PendingPlan = null;
        _pendingPlanReviewComments = null;
        _pendingPermissionResponse?.TrySetException(new OperationCanceledException(reason));
        _pendingPermissionResponse = null;
        PendingPermission = null;
        _pendingElicitationResponse?.TrySetResult(new ElicitationAnswer(ElicitationAction.Cancel, _emptyElicitationContent));
        _pendingElicitationResponse = null;
        PendingElicitation = null;
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
                var session = await RequestNewSessionAsync(connection, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(connection, _connection) || NeedsAuthentication)
                    throw new InvalidOperationException("The agent disconnected before the session was ready.");
                IsSignedIn = true;
                NeedsAuthentication = false;
                AdoptNewSession(session);
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

    private Task<NewSessionResult> RequestNewSessionAsync(IAcpAgentConnection connection, CancellationToken cancellationToken)
    {
        _pendingCommandCatalogs = new Dictionary<string, IReadOnlyList<AvailableCommand>>(StringComparer.Ordinal);
        return connection.NewSessionAsync(WorkspaceCwd, null, cancellationToken);
    }

    private void AdoptNewSession(NewSessionResult session)
    {
        _sessionId = session.SessionId;
        if (_pendingCommandCatalogs is { } buffered && buffered.TryGetValue(session.SessionId, out var commands))
            ApplyCommandCatalog(commands);
        _pendingCommandCatalogs = null;
        ApplyConfigOptions(session.ConfigOptions);
        OnSessionStarted();
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
        var autoOffered = AutoServices?.EffortClassifier is not null && _effortOption is { } effort &&
            AutoEffortValues.All(value => effort.Options.Any(candidate => candidate.Value == value));
        if (autoOffered) AvailableEfforts.Insert(0, _autoEffort);
        _isAutoEffort = _autoEffortSelected && autoOffered;
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
                    if (IsBusy) break;
                    EnsureUserMessage().AppendText(chunk.Text);
                    UpdateSessionTitleFromFirstUserMessage();
                    break;
                case SessionUpdate.AgentMessageChunk chunk:
                    EnsureAssistantMessage().AppendText(chunk.Text);
                    UpdateActivity("Responding…");
                    break;
                case SessionUpdate.AgentThoughtChunk thought:
                    EnsureAssistantMessage().AppendThought(thought.Text);
                    UpdateActivity("Thinking…");
                    break;
                case SessionUpdate.ToolCall toolCall:
                    _toolCallLocations.UnionWith(toolCall.Call.Locations);
                    UpsertToolCall(toolCall.Call);
                    break;
                case SessionUpdate.Plan plan:
                    CurrentPlan = new PlanViewModel(plan.Entries) { IsExpanded = CurrentPlan?.IsExpanded ?? true };
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
                case SessionUpdate.TurnEnded turnEnded:
                    if (_submittedPrompts.Count > 1)
                    {
                        if (turnEnded.StopReason != "cancelled") _currentAssistantMessage = null;
                        break;
                    }
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
                    _toolCallDiffsById.Clear();
                    UpdateActivity("Working…");
                    _ = RefreshUsageAsync();
                    break;
            }
        });
    }

    private readonly HashSet<string> _runningSubagents = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>How many subagents (Claude Code's Agent tool) are running.</summary>
    public int RunningAgentCount => _runningSubagents.Count;

    /// <summary>Label for <see cref="RunningAgentCount"/>: "1 agent", "3 agents".</summary>
    public string RunningAgentsLabel => RunningAgentCount == 1 ? "1 agent" : RunningAgentCount + " agents";

    private void NotifyRunningAgents()
    {
        OnPropertyChanged(nameof(RunningAgentCount));
        OnPropertyChanged(nameof(RunningAgentsLabel));
    }

    private void ClearRunningSubagents()
    {
        if (_runningSubagents.Count == 0) return;
        _runningSubagents.Clear();
        NotifyRunningAgents();
    }

    private void UpsertToolCall(ToolCallUpdate call)
    {
        if (IsBusy)
        {
            var diffs = ResolveToolCallDiffs(call);
            if (diffs.Count > 0) _ = Task.Run(() => TrackToolCallFileChangesAsync(call, diffs));
        }
        var message = EnsureAssistantMessage();
        var card = message.ToolCalls.FirstOrDefault(t => t.ToolCallId == call.ToolCallId);
        if (card is not null)
        {
            card.Apply(call);
        }
        else
        {
            card = new ToolCallCardViewModel(call);
            message.ToolCalls.Add(card);
            message.AppendToolCall(card);
        }
        if (card.IsSubagent || _runningSubagents.Contains(card.ToolCallId))
        {
            bool changed = card.Status is ToolCallStatus.Completed or ToolCallStatus.Failed
                ? _runningSubagents.Remove(card.ToolCallId)
                : _runningSubagents.Add(card.ToolCallId);
            if (changed) NotifyRunningAgents();
        }
        UpdateActivity("Working…");
    }

    private ChatMessageViewModel EnsureAssistantMessage()
    {
        _currentUserMessage = null;
        if (_currentAssistantMessage is null)
        {
            _currentAssistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
            Messages.Add(_currentAssistantMessage);
        }
        return _currentAssistantMessage;
    }

    private ChatMessageViewModel EnsureUserMessage()
    {
        _currentAssistantMessage = null;
        if (_currentUserMessage is null)
        {
            _currentUserMessage = new ChatMessageViewModel(ChatRole.User);
            Messages.Add(_currentUserMessage);
        }
        return _currentUserMessage;
    }

    private void ObserveEnd(Task request, Action onEnd) =>
        _ = request.ContinueWith(finished =>
        {
            _ = finished.Exception;
            RunOnUi(() => { if (!_disposed) onEnd(); });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

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
            if (_pendingPlan is { IsResolved: false }) _pendingPlan.MarkResolved("Superseded by a newer request");
            _pendingPermissionResponse = e.Response;
            PlanReviewViewModel? plan = null;
            void Choose(PermissionOption option)
            {
                if (!e.Response.TrySetResult(option.OptionId)) return;
                _pendingPermissionResponse = null;
                RunEachStepReportingFailures(
                    () => PendingPermission = null,
                    () =>
                    {
                        if (plan is { IsResolved: false })
                        {
                            plan.MarkResolved(option.Outcome is PermissionOutcome.AllowOnce or PermissionOutcome.AllowAlways
                                ? "Plan accepted — implementing…" : "Plan rejected");
                        }
                    },
                    () => UpdateActivity("Working…"));
            }

            PendingPermission = new PermissionRequestViewModel(ToolDisplayName.Describe(e.Call.Title), e.Options, Choose);
            UpdateActivity("Waiting for permission…");
            ObserveEnd(e.Response.Task, () =>
            {
                if (!ReferenceEquals(_pendingPermissionResponse, e.Response)) return;
                _pendingPermissionResponse = null;
                PendingPermission = null;
                if (plan is { IsResolved: false }) plan.MarkResolved("Request ended");
            });

            var planText = e.Call.Kind == "switch_mode"
                ? string.Join("\n", e.Call.Content.Where(content => !content.IsDiff && !string.IsNullOrWhiteSpace(content.Text)).Select(content => content.Text))
                : string.Empty;
            if (planText.Length > 0)
            {
                plan = new PlanReviewViewModel(planText, e.Options, Choose,
                    comments =>
                    {
                        if (plan!.RejectOption is not PermissionOption reject) return;
                        _pendingPlanReviewComments = comments;
                        RunEachStepReportingFailures(() => plan.MarkResolved("Sent back for revision"));
                        Choose(reject);
                        if (!IsBusy) SendPendingPlanReview();
                    });
                PendingPlan = plan;
                PlanReviewRequested?.Invoke(this, EventArgs.Empty);
                RaiseAttention(ChatAttentionKind.PlanReview, "Claude has a plan for you", "Review or approve the implementation plan.");
            }
            else
            {
                RaiseAttention(ChatAttentionKind.PermissionNeeded, "Claude needs your permission", ToolDisplayName.Describe(e.Call.Title));
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
                if (!ReferenceEquals(_pendingElicitationResponse, e.Response)) return;
                _pendingElicitationResponse = null;
                PendingElicitation = null;
            });
            ObserveEnd(e.Response.Task, () =>
            {
                if (!ReferenceEquals(_pendingElicitationResponse, e.Response)) return;
                _pendingElicitationResponse = null;
                PendingElicitation = null;
            });
            RaiseAttention(ChatAttentionKind.PermissionNeeded, "Claude needs your input", e.Message);
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
                var lines = SplitLinesKeepingTerminators(text);
                var start = Math.Max(0, (e.Line ?? 1) - 1);
                var count = e.Limit ?? Math.Max(0, lines.Length - start);
                text = JoinRequestedLines(lines, start, count);
            }

            e.Response.TrySetResult(text);
        }
        catch (Exception ex) { e.Response.TrySetException(ex); }
    }

    private static string[] SplitLinesKeepingTerminators(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            lines.Add(text.Substring(start, index - start + 1));
            start = index + 1;
        }

        if (start < text.Length) lines.Add(text.Substring(start));
        return lines.ToArray();
    }

    private static string JoinRequestedLines(string[] lines, int start, int count)
    {
        if (start >= lines.Length || count <= 0) return string.Empty;
        var end = (int)Math.Min(lines.Length, (long)start + count);
        var builder = new StringBuilder();
        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            if (index == end - 1)
            {
                if (line.EndsWith("\r\n", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 2);
                else if (line.Length > 0 && line[line.Length - 1] == '\n') line = line.Substring(0, line.Length - 1);
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private async void OnFileWriteRequested(object? sender, FileWriteRequestEventArgs e)
    {
        ChangedFileViewModel? created = null;
        try
        {
            var (tracked, isNewRow) = await TrackChangeBeforeWriteAsync(e.Path).ConfigureAwait(true);
            if (isNewRow) created = tracked;
            using var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, e.Path);
            await WriteLeasedFileAsync(pathLease, e.Content).ConfigureAwait(true);
            RunOnUi(() => tracked.UpdateCounts(e.Content));
            e.Response.TrySetResult(true);
        }
        catch (Exception ex)
        {
            if (created is not null) RunOnUi(() => UntrackChange(created));
            e.Response.TrySetException(ex);
        }
    }

    private readonly Dictionary<string, ChangedFileViewModel> _changedFilesByPath = new Dictionary<string, ChangedFileViewModel>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<ToolCallContent>> _toolCallDiffsById = new Dictionary<string, List<ToolCallContent>>(StringComparer.Ordinal);

    private readonly HashSet<string> _toolCallLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private List<ToolCallContent> ResolveToolCallDiffs(ToolCallUpdate call)
    {
        var finished = call.Status is ToolCallStatus.Completed or ToolCallStatus.Failed;
        var diffs = call.Content.Where(content => content.IsDiff && !string.IsNullOrWhiteSpace(content.Path)).ToList();
        if (diffs.Count > 0) _toolCallDiffsById[call.ToolCallId] = diffs;
        else if (finished && _toolCallDiffsById.TryGetValue(call.ToolCallId, out var reported)) diffs = reported;
        if (finished) _toolCallDiffsById.Remove(call.ToolCallId);
        return diffs;
    }

    private async Task TrackToolCallFileChangesAsync(ToolCallUpdate call, List<ToolCallContent> diffs)
    {
        try
        {
            foreach (var content in diffs)
            {
                ChangedFileViewModel tracked;
                try { (tracked, _) = await TrackChangeBeforeWriteAsync(content.Path!, content, call.Status, call.ToolCallId).ConfigureAwait(true); }
                catch (Exception) { continue; }

                if (call.Status is not (ToolCallStatus.Completed or ToolCallStatus.Failed)) continue;
                string? current;
                using (var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, tracked.FullPath))
                    current = await ReadLeasedFileAsync(pathLease).ConfigureAwait(true);
                string? snapshot;
                lock (_changedFilesByPath) snapshot = tracked.OriginalText;
                var unchanged = string.Equals(current, snapshot, StringComparison.Ordinal);
                RunOnUi(() =>
                {
                    if (call.Status == ToolCallStatus.Failed)
                    {
                        if (unchanged) UntrackChange(tracked);
                        return;
                    }
                    tracked.UpdateCounts(current ?? string.Empty);
                    if (unchanged) tracked.MarkNotRevertable();
                });
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task<(ChangedFileViewModel Entry, bool Created)> TrackChangeBeforeWriteAsync(
        string requestedPath, ToolCallContent? diff = null, ToolCallStatus status = ToolCallStatus.Completed, string? toolCallId = null)
    {
        using var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, requestedPath);
        lock (_changedFilesByPath)
        {
            if (_changedFilesByPath.TryGetValue(pathLease.FullPath, out var existing))
            {
                if (toolCallId is not null && existing.CreatedByToolCallId == toolCallId
                    && TryGetRaceCorrectedSnapshot(existing.OriginalText, status, diff, out var corrected))
                {
                    existing.CorrectOriginalSnapshot(corrected);
                    if (corrected.Length == 0 && existing.TryMarkNotRevertable()) RunOnUi(existing.NotifyRevertabilityChanged);
                }

                return (existing, false);
            }
        }

        var original = await ReadLeasedFileAsync(pathLease).ConfigureAwait(true);
        var raceCorrected = TryGetRaceCorrectedSnapshot(original, status, diff, out var correctedOriginal);
        if (raceCorrected) original = correctedOriginal;

        var entry = new ChangedFileViewModel(pathLease.FullPath, original,
            file => OnUiAsync(() => AcceptChangeAsync(file)),
            file => OnUiAsync(() => RejectChangeAsync(file)),
            toolCallId);
        if (diff is not null && !IsPreEditSnapshot(original, diff)) entry.MarkNotRevertable();
        if (raceCorrected && original is { Length: 0 }) entry.MarkNotRevertable();
        lock (_changedFilesByPath)
        {
            if (_changedFilesByPath.TryGetValue(pathLease.FullPath, out var raced))
            {
                if (toolCallId is not null && raced.CreatedByToolCallId == toolCallId
                    && TryGetRaceCorrectedSnapshot(raced.OriginalText, status, diff, out var late))
                {
                    raced.CorrectOriginalSnapshot(late);
                    if (late.Length == 0 && raced.TryMarkNotRevertable()) RunOnUi(raced.NotifyRevertabilityChanged);
                }

                return (raced, false);
            }

            _changedFilesByPath[pathLease.FullPath] = entry;
        }

        RunOnUi(() =>
        {
            bool live;
            lock (_changedFilesByPath) live = _changedFilesByPath.TryGetValue(entry.FullPath, out var current) && ReferenceEquals(current, entry);
            if (live) ChangedFiles.Add(entry);
        });
        return (entry, true);
    }

    private static bool TryGetRaceCorrectedSnapshot(
        string? snapshot, ToolCallStatus status, ToolCallContent? diff, out string corrected)
    {
        if (status == ToolCallStatus.Completed && snapshot is not null && diff?.OldText is { } preEditText
            && diff.NewText is { } postEditText
            && string.Equals(FoldLineEndings(snapshot), FoldLineEndings(postEditText), StringComparison.Ordinal))
        {
            corrected = preEditText;
            return true;
        }

        corrected = string.Empty;
        return false;
    }

    private static string FoldLineEndings(string text) => text.Replace("\r\n", "\n");

    private static bool IsPreEditSnapshot(string? snapshot, ToolCallContent diff)
    {
        if (snapshot is null) return diff.OldText is null;
        var text = FoldLineEndings(snapshot);
        return diff.OldText is { } oldText
            ? text.IndexOf(FoldLineEndings(oldText), StringComparison.Ordinal) >= 0
            : !string.Equals(text, FoldLineEndings(diff.NewText!), StringComparison.Ordinal);
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

    private async Task OpenChangedFileAsync(ChangedFileViewModel? file)
    {
        if (file is null) return;
        var workspaceRoot = _services.WorkspaceRoot;
        try
        {
            await Task.Run(async () =>
            {
                using var pathLease = WorkspacePathGuard.AcquireFile(workspaceRoot, file.FullPath);
                using var document = pathLease.ProtectDocument();
                if (document is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    throw new FileNotFoundException("The workspace file does not exist.", pathLease.FullPath);
                await _services.OpenDocumentAsync(pathLease.FullPath, null, _lifetime.Token).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not open {file.Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens a file reference the user clicked in the transcript. Accepts only a link produced by
    /// <see cref="ChatFileReference"/> whose path lies within the workspace. Never faults.
    /// </summary>
    public async Task OpenFileReferenceAsync(string? href)
    {
        if (!ChatFileReference.TryParseLink(href, out var reference, out var line)) return;

        try
        {
            var workspaceRoot = _services.WorkspaceRoot;
            if (string.IsNullOrEmpty(workspaceRoot))
                throw new InvalidOperationException("no folder or solution is open.");

            var toolCallLocations = _toolCallLocations.ToArray();
            var cancellationToken = _lifetime.Token;
            await Task.Run(async () =>
            {
                var candidate = ResolveFileReference(workspaceRoot!, reference, toolCallLocations, cancellationToken);
                using var pathLease = WorkspacePathGuard.AcquireFile(workspaceRoot, candidate);
                using var document = pathLease.ProtectDocument();
                if (document is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    throw new FileNotFoundException("the file does not exist.", pathLease.FullPath);
                await _services.OpenDocumentAsync(pathLease.FullPath, line, cancellationToken).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not open {reference}: {ex.Message}";
        }
    }

    private static string ResolveFileReference(string workspaceRoot, string reference, IReadOnlyList<string> toolCallLocations, CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(reference)) return reference;

        var underRoot = Path.Combine(workspaceRoot, reference);
        if (WorkspacePathGuard.TryResolveWithinWorkspace(workspaceRoot, underRoot, out var resolvedUnderRoot) && File.Exists(resolvedUnderRoot))
            return underRoot;

        var suffix = Path.DirectorySeparatorChar + reference.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refused = false;
        foreach (var location in toolCallLocations)
        {
            if (!location.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryAnchorLocation(workspaceRoot, location, out var anchored)) continue;
            if (!WorkspacePathGuard.TryResolveWithinWorkspace(workspaceRoot, anchored, out var fullPath))
                refused = true;
            else if (File.Exists(fullPath))
                matches.Add(fullPath);
        }

        if (matches.Count > 1)
            throw new IOException("more than one file Claude worked on matches it; ask Claude for the full path.");
        if (matches.Count == 1) return matches.First();
        if (refused)
            throw new UnauthorizedAccessException("the file Claude worked on by that name is outside the workspace or cannot be resolved safely.");

        foreach (var found in WorkspaceFileSearch.FindBySuffix(workspaceRoot, suffix, cancellationToken))
        {
            if (WorkspacePathGuard.TryResolveWithinWorkspace(workspaceRoot, found, out var fullPath) && File.Exists(fullPath))
                matches.Add(fullPath);
        }

        if (matches.Count > 1)
            throw new IOException("more than one file in the workspace has that name; ask Claude for the full path.");
        return matches.Count == 1 ? matches.First() : underRoot;
    }

    private static bool TryAnchorLocation(string workspaceRoot, string location, out string anchored)
    {
        anchored = string.Empty;
        if (location.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        anchored = Path.IsPathRooted(location) ? location : Path.Combine(workspaceRoot, location);
        return true;
    }

    private async Task RejectChangeAsync(ChangedFileViewModel file)
    {
        try
        {
            await RevertChangeAsync(file).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not revert {file.Name}: {ex.Message}";
        }
    }

    private async Task RejectAllChangesAsync()
    {
        var files = ChangedFiles.ToList();
        var failed = 0;
        foreach (var file in files)
        {
            try { await RevertChangeAsync(file).ConfigureAwait(true); }
            catch (Exception) { failed++; }
        }

        if (failed > 0 && !_disposed) StatusMessage = $"Could not revert {failed} of {files.Count} files.";
    }

    private async Task RevertChangeAsync(ChangedFileViewModel file)
    {
        bool canRevert;
        string? original;
        lock (_changedFilesByPath)
        {
            canRevert = file.CanRevert;
            original = file.OriginalText;
        }

        if (!canRevert) throw new InvalidOperationException("The content from before the edit is not known.");
        var workspaceRoot = _services.WorkspaceRoot;
        await Task.Run(async () =>
        {
            using var pathLease = WorkspacePathGuard.AcquireFile(workspaceRoot, file.FullPath);
            if (original is null) File.Delete(pathLease.FullPath);
            else await WriteLeasedFileAsync(pathLease, original).ConfigureAwait(true);
        }).ConfigureAwait(true);
        UntrackChange(file);
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
            StatusMessage = ex is not null ? $"Agent disconnected: {ex.Message}" : "Agent disconnected.";
            _ = ReleaseConnectionAsync();
        });
    }

    private async Task ReleaseConnectionAsync()
    {
        _autoEffortStop?.Cancel();
        var connection = _connection;
        _connection = null;
        _sessionId = null;
        StatusMessage = WithQueueNotice(StatusMessage, DiscardQueuedMessages());
        _pendingCommandCatalogs = null;
        _availableCommands = Array.Empty<AvailableCommand>();
        _hasCommandCatalog = false;
        RefreshSlashSuggestions();
        ActivityText = string.Empty;
        IsSignedIn = _services.AuthService.CurrentState == AuthState.SignedIn;
        ClearPendingRequests("The agent connection was closed.");
        ClearRunningSubagents();
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
        _services.WorkspaceRootChanged -= OnWorkspaceRootChanged;
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
            _connectGate.Dispose();
            _lifetime.Dispose();
        }
    }
}
