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
    private string? _workspaceRoot;
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

    // Never advertised by the adapter (it deliberately excludes login/logout from the commands it
    // sends, see issue #34): these are added locally so the popup can offer them even
    // when there is no session at all, which is exactly the state /login exists to get out of.
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

    /// <summary>How many consecutive failed fetches still get the short retry interval. The retry
    /// exists for the startup race (VS's inherited PATH/auth not settled yet), which resolves in
    /// seconds; past that a failure means usage is simply unavailable, and hammering an agent that
    /// keeps failing every 15s for the rest of the session buys nothing.</summary>
    private const int UsagePollFastRetries = 4;

    /// <summary>How long <see cref="UsagePollingLoopAsync"/> waits before its next attempt: the
    /// short retry interval while the startup race is still plausible, the normal cadence once a
    /// fetch has succeeded or the fast retries are spent. Extracted so the decision itself - not
    /// just the constants - is directly unit-testable without waiting out either real interval.</summary>
    internal static TimeSpan NextUsagePollDelay(int consecutiveFailures) =>
        consecutiveFailures > 0 && consecutiveFailures <= UsagePollFastRetries ? UsagePollRetryInterval : UsagePollInterval;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _emptyElicitationContent =
        new Dictionary<string, IReadOnlyList<string>>();

    public ChatViewModel(IChatSessionServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ChatViewModel must be constructed on a thread with a SynchronizationContext (e.g. the WPF UI thread).");
        // A send that starts a turn keeps executing until the turn ends; without concurrent
        // executions the command would report CanExecute=false for that whole time and make the
        // mid-turn queue (see EnqueueDraft) unreachable from the Send button and Enter key.
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
                // Stale for the UI, but on the agent that session is now published: nothing else
                // will ever address it again, so take it back down.
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
            // The session changed while this call was in flight, so its OnSessionStarted found the
            // toggle busy and skipped the startup enable. Issue it now for whichever session is current.
            if (!_disposed && _services.RemoteControlAtStartup && _sessionId is not null
                && (sessionId != _sessionId || !ReferenceEquals(connection, _connection)))
            {
                _ = SetRemoteControlAsync(true);
            }
        }
    }

    // The agent keeps every session it created, so one left published to claude.ai/code stays
    // there: enabled, invisible, and unreachable from a toggle that only addresses the current
    // session. Best effort - the session is being left either way.
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

    // Before session/new or session/load replaces _sessionId. Only an acknowledged disable clears
    // the UI state: a switch that then fails leaves the user on this session, and the toggle must
    // still tell the truth about it.
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

    // A new or resumed session starts with Remote Control off; the option turns it on right away.
    private void OnSessionStarted()
    {
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

    // _isSwitchingSession covers the session/new and session/load round trips: neither IsBusy nor
    // IsConnecting is set for their duration, so without it a prompt accepted mid-switch is sent to
    // the outgoing session and then wiped from the transcript by ResetTranscriptState.
    private bool CanEditDraft => CanQueueOrSendDraft && !IsBusy;

    // /login and /logout exist precisely to get out of NeedsAuthentication, so their popup bypasses
    // CanEditDraft's NeedsAuthentication/IsConnecting/IsConfigBusy gates. It still respects
    // IsBusy/_isSwitchingSession/disposal: those describe a turn or session switch actually in
    // flight, not "no session because signed out".
    private bool CanShowSlashPopup => !_disposed && !IsBusy && !_isSwitchingSession;

    // Same admission checks as CanEditDraft, minus IsBusy: a turn already in flight must not block
    // composing and sending the next message - it gets queued (see SendCoreAsync/EnqueueDraft) and
    // dispatched straight away to an agent that queues prompts itself, otherwise once the current
    // turn ends - instead of being blocked until then.
    private bool CanQueueOrSendDraft => !_disposed && !NeedsAuthentication && !IsConnecting
        && !IsConfigBusy && !_isSwitchingSession;

    /// <summary>The working directory this client trusts - never a path the agent reported.</summary>
    private string WorkspaceCwd => _services.WorkspaceRoot ?? Environment.CurrentDirectory;

    // Deliberately does not require !IsBusy: SetSessionConfigOptionAsync is its own ACP RPC call
    // over the same JSON-RPC connection as an in-flight prompt, which already supports concurrent
    // in-flight requests (matched by request id) - there's no protocol reason model/mode/effort
    // can't change mid-turn, and other clients (the reference VS Code extension, the CLI) let you.
    // It does require the switch to be over: session/load publishes _sessionId up front while the
    // previous session's pickers are still populated, and the load can still fail and roll back.
    public bool CanConfigure => !_disposed && !NeedsAuthentication && !IsConnecting && !IsConfigBusy &&
        !_isCapturingDocument && !_isSwitchingSession && _sessionId is not null;
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
        var title = SessionTitleFormat.Describe(Messages[0].Text, sessionId: null);
        if (title.Length > 0) SessionTitle = title;
    }

    /// <summary>Normalizes an agent-reported session title through the same rule as a locally
    /// derived one. It is bound straight into the single-row panel header and its tooltip, where an
    /// embedded line break reflows the toolbar and a huge string hangs WPF's measure pass - and it
    /// crosses the untrusted boundary, unlike the prompt text. Null means "no title of its own".</summary>
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
                // Usage is best-effort presentation, never allowed to affect the chat session itself.
                consecutiveFailures++;
            }

            // On startup, VS's inherited PATH/auth state may not be settled yet, so the first
            // fetch(es) can transiently fail. Retry soon instead of leaving the usage button hidden
            // for a full poll interval - then settle back to the normal cadence, whether because a
            // fetch succeeded or because the fast retries are spent (see NextUsagePollDelay).
            TimeSpan delay = NextUsagePollDelay(consecutiveFailures);
            try
            {
                await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
            }
            // Dispose() cancels before it disposes, but an iteration preempted between the loop's
            // cancellation check and this call reads _lifetime.Token after disposal, which throws
            // ObjectDisposedException and would fault the discarded task.
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

    // "t" is the culture's own short time. A literal "h:mm tt" is a 12-hour clock whose meridiem
    // is empty under net472's NLS data for de-DE, fr-FR, it-IT and others, showing 15:00 as "3:00 ".
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
                OnPropertyChanged(nameof(ScopedWeeklyUsage));
            }
        }
    }

    public UsageLimitDisplay? SessionUsage => BuildUsageDisplay("Current session", limit => limit.Kind == "session");

    public UsageLimitDisplay? WeeklyUsage => BuildUsageDisplay("This week", limit => limit.Kind == "weekly_all");

    /// <summary>The agent's <c>weekly_scoped</c> limit, labelled with whatever scope the agent
    /// named. Nothing about it is product-specific, so the fallback must not invent a product name.</summary>
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

    private void OnWorkspaceRootChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_disposed) return;
        var root = TryReadWorkspaceRoot();
        // A closed solution - or a root the host cannot report right now - is not a project switch:
        // the conversation still belongs to the project it was started in, and closing a solution is
        // also the first half of reloading the same one. Only a different root switches projects.
        if (root is null || string.Equals(root, _workspaceRoot, StringComparison.OrdinalIgnoreCase)) return;
        _workspaceRoot = root;
        _ = SwitchWorkspaceAsync();
    });

    /// <summary>Drops the previous project's session, transcript and agent process, then reconnects
    /// so the empty chat is immediately usable in the new one. The agent is spawned with the
    /// workspace root as its working directory and can never follow a switch, so reusing the
    /// connection would leave the new project talking to the old project's process.</summary>
    private async Task SwitchWorkspaceAsync()
    {
        // Tearing the outgoing agent down is an await, and the old transcript stays on screen for
        // its duration. Without this flag the composer is live over it, and a prompt accepted there
        // is sent to a session ResetTranscriptState is about to erase - the hazard documented on
        // CanEditDraft and already guarded by NewSessionCoreAsync and OpenSessionCoreAsync.
        _isSwitchingSession = true;
        // Cleared before the teardown, not after it: the outgoing workspace's status is stale from
        // here on, and ReleaseConnectionAsync is about to report anything the switch costs the user.
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
            // Nothing awaits this handler, so an escaping exception would be unobserved on the UI
            // thread. The cached root was advanced before the work started; drop it so the next
            // event for the same root retries instead of being suppressed as a duplicate.
            _workspaceRoot = null;
            if (!_disposed) StatusMessage = $"Could not switch to the new workspace: {ex.Message}";
        }
        finally
        {
            _isSwitchingSession = false;
            NotifyStateChanged();
        }
    }

    /// <summary>The host reads live solution state for this, which can throw while a solution is
    /// closing or reloading; an unknown root is treated as "no switch" rather than a reason to
    /// discard a conversation.</summary>
    private string? TryReadWorkspaceRoot()
    {
        try { return _services.WorkspaceRoot; }
        catch { return null; }
    }

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
            SendPendingPlanReview();
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
            SendPendingPlanReview();
            // A turn can end while this RPC is still in flight, and the queue refuses to dispatch
            // into a config change; this is the blocker lifting, so whatever it held back goes now.
            DispatchNextQueuedMessage();
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

    // /login and /logout bypass CanQueueOrSendDraft's NeedsAuthentication/IsConnecting/IsConfigBusy
    // gates on purpose - NeedsAuthentication is exactly the state /login exists to resolve. They are
    // never queued, so they are refused outright while a turn or session switch is in flight
    // (CanShowSlashPopup) and while one of them is already running (no second concurrent console).
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

    public Task SendAsync() => OnUiAsync(SendCoreAsync);

    // A message typed and sent while a turn is already in flight: shown in the transcript right
    // away (dimmed, see ChatMessageViewModel.IsPending) so the user can see it was captured, held
    // here until it can be dispatched exactly like a normal send - straight away when the agent
    // queues prompts itself (IAcpAgentConnection.SupportsPromptQueueing), otherwise once the
    // in-flight turn ends.
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

    // Every prompt handed to the agent whose session/prompt has not returned yet, oldest first
    // (null = a live send, which only ever starts when nothing else is in flight). Only the oldest
    // is running; the rest wait in the agent's own queue, and the agent settles the running one when
    // it takes the next one in.
    private readonly List<QueuedMessage?> _submittedPrompts = new List<QueuedMessage?>();

    // RunTurnAsync calls not yet finished - including prompts dropped from _submittedPrompts with their
    // session whose session/prompt is still to return. IsBusy covers all of them.
    private int _runningTurns;

    // Set by Stop until every running turn has returned (or the cancel fails): nothing is sent ahead
    // into a turn that is being cancelled, since the agent would settle it unstarted.
    private bool _isStopping;

    // Sent-ahead messages that came back before they were confirmed started: refused, the agent
    // died, or Stop answered them "cancelled". Once nothing is running they go back to the front of
    // the queue, in order, and are sent again - Stop stops the work, not the conversation. (Claude
    // Code may already have folded a stopped one into the cancelled turn; the agent answers both the
    // same way, and the user asked for it to be answered.) If the last prompt failed too they go into
    // the message box instead (see RunTurnAsync). Dropped only with their session
    // (DiscardQueuedMessages), which tells the user.
    private readonly List<QueuedMessage> _returnedUnstarted = new List<QueuedMessage>();

    private Task SendCoreAsync()
    {
        if (!CanSend()) return Task.CompletedTask;

        // Snapshot now, not after the connect round trip: the composer stays editable mid-turn, so
        // whatever is typed while EnsureConnectedAsync is still running belongs to the *next*
        // message and must neither be folded into this prompt nor cleared with it.
        var text = InputText.Trim();
        if (TryGetClientCommand(text, out var clientCommand))
        {
            // Never a prompt: intercepted here, before the queue exists, so it can never be
            // buffered and later replayed into session/prompt by DispatchQueuedMessageAsync.
            return RunClientCommandAsync(clientCommand);
        }

        // Sending is the user's own action, so it is what clears a stale error - not the start of
        // every turn, which would wipe the failure of the turn that just ended before it could be
        // read (an auto-dispatched queue would erase its own predecessor's error).
        StatusMessage = null;
        var attachments = Attachments.ToArray();

        if (IsBusy)
        {
            EnqueueDraft(text, attachments);
            return Task.CompletedTask;
        }

        return RunTurnAsync(null, () =>
        {
            // Acquire before consuming the draft: failed startup must not lose text or attachments.
            // RunTurnAsync's own EnsureConnectedAsync has already succeeded by the time this runs.
            Messages.Add(BuildUserBubble(text, attachments, isPending: false));
            UpdateSessionTitleFromFirstUserMessage();
            ConsumeDraft(text, attachments);
            return (text, (IReadOnlyList<ChatAttachmentViewModel>)attachments);
        });
    }

    // /login and /logout never touch the turn/queue machinery above: they are local actions against
    // IAcpAuthService, not agent prompts. IsAuthCommandRunning is this method's own busy flag (CanSend
    // already refuses a second concurrent call) so it can run independently of NeedsAuthentication.
    private async Task RunClientCommandAsync(ClientSlashCommand command)
    {
        if (!CanRunClientCommand) return;
        bool login = command == ClientSlashCommand.Login;
        InputText = string.Empty;
        StatusMessage = null;

        IsAuthCommandRunning = true;
        _authCommandCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _authCommandCts.Token;
        bool signedIn = false;
        try
        {
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
        // Only the user's Cancel (or disposal) is a cancellation; any other OperationCanceledException
        // is a failure of the command itself and is reported as one below.
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_disposed) StatusMessage = login ? "Sign-in cancelled." : "Sign-out cancelled.";
        }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"{(login ? "Sign-in" : "Sign-out")} failed: {ex.Message}";
        }
        finally
        {
            _authCommandCts?.Dispose();
            _authCommandCts = null;
            IsAuthCommandRunning = false;
        }

        // After the sign-in itself has been reported, so a connect failure (which InitializeCoreAsync
        // reports on its own) is what the user reads last, never hidden behind "Signed in".
        if (signedIn && !_disposed) await InitializeCoreAsync(_lifetime.Token).ConfigureAwait(true);
    }

    // Draft is consumed immediately (unlike the live-send path above) so the composer is free for
    // the next message right away; there is no "connect first" step to guard here since dispatch -
    // and therefore the connection attempt - happens later, once DispatchNextQueuedMessage dequeues
    // this entry and DispatchQueuedMessageAsync runs it through RunTurnAsync.
    private void EnqueueDraft(string text, ChatAttachmentViewModel[] attachments)
    {
        var bubble = BuildUserBubble(text, attachments, isPending: true);
        Messages.Add(bubble);
        UpdateSessionTitleFromFirstUserMessage();
        ConsumeDraft(text, attachments);
        _queuedMessages.Enqueue(new QueuedMessage(bubble, text, attachments));
        DispatchNextQueuedMessage();
    }

    // Images render as thumbnails on the bubble; only documents keep a text placeholder.
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

    // Takes exactly what this message carried out of the composer and leaves anything typed or
    // attached since alone: that newer draft is the user's next message, not part of this one.
    private void ConsumeDraft(string text, ChatAttachmentViewModel[] attachments)
    {
        if (InputText.Trim() == text) InputText = string.Empty;
        foreach (var attachment in attachments) Attachments.Remove(attachment);
        AttachmentError = null;
    }

    // Mirrors SendCoreAsync's live-send path for a message that was queued earlier: the bubble
    // already exists in the transcript, and RunTurnAsync stops it reading as pending once the agent
    // is actually running it.
    private Task DispatchQueuedMessageAsync(QueuedMessage queued) =>
        RunTurnAsync(queued, () => (queued.Text, queued.Attachments));

    // Shared by SendCoreAsync and DispatchQueuedMessageAsync: everything from connecting through
    // submitting one turn's content and reacting to how it ended is identical between a live send
    // and a queued dispatch - only how (text, attachments) is obtained differs, which is why that
    // step is the one thing left to the caller. `prepare` runs only after EnsureConnectedAsync has
    // already succeeded, preserving the live-send path's "acquire before consuming the draft" rule.
    // A call made while another turn is running (only ever a queued message sent ahead to an agent
    // that queues prompts) joins that turn's busy state instead of starting a new one.
    private async Task RunTurnAsync(QueuedMessage? queued, Func<(string Text, IReadOnlyList<ChatAttachmentViewModel> Attachments)> prepare)
    {
        bool joining = _runningTurns++ > 0;
        if (!joining)
        {
            ActivityText = "Working…";
            IsBusy = true;
            _turnStartedAt = DateTimeOffset.UtcNow;
            _turnStartUsedTokens = _sessionUsedTokens;
            TurnTokens = null;
        }
        bool submitted = false;
        bool failed = false;
        string? stopReason = null;
        try
        {
            var (connection, sessionId) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed) return;
            var (text, attachments) = prepare();
            var content = new List<ContentBlock>(attachments.Count + 1);
            if (text.Length > 0) content.Add(new ContentBlock.Text(text));
            foreach (var attachment in attachments)
                content.Add(attachment.ToContentBlock());

            // A joining prompt waits in the agent's queue: the running turn keeps its bubble.
            if (!joining) _currentAssistantMessage = null;
            _submittedPrompts.Add(queued);
            submitted = true;
            if (_submittedPrompts.Count == 1) queued?.Bubble.MarkSent();
            // Once the running prompt is submitted, acceptance is ambiguous on transport failure: it is
            // not restored or resent. (A sent-ahead prompt is judged in OnPromptReturned.)
            stopReason = await connection.SendPromptAsync(sessionId, content, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            failed = true;
            if (!_disposed) StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            if (submitted) OnPromptReturned(queued, stopReason);
            if (--_runningTurns == 0)
            {
                IsBusy = false;
                ActivityText = string.Empty;
                _currentAssistantMessage = null;
                // A stopped or failed turn sends no final status for the subagents it cut short.
                if (_isStopping || failed) ClearRunningSubagents();
                _isStopping = false;
                // When the last prompt failed - the agent errored or died - follow-ups that came back
                // unstarted go back into the message box: resending could go to a dead pipe, and
                // waiting for a disconnect that may never come would strand them. Anything queued
                // behind them follows them there in order, so a later message never reaches Claude
                // without an earlier one. Otherwise (Stop included) they are sent again.
                if (failed && _returnedUnstarted.Count > 0)
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

        if (_runningTurns > 0) return;
        SendPendingPlanReview();
        DispatchNextQueuedMessage();
    }

    // Whether a returning prompt ran is decided here, when its answer arrives, not when Stop is
    // pressed. A prompt runs once it is confirmed started: sent while nothing else was in flight, or
    // taken in by a hand-off - either way its bubble stops reading as pending. One still pending that
    // comes back "cancelled" (Stop) or failed (refused, or the agent died) is held in
    // _returnedUnstarted: once nothing is running it is sent again, or goes back into the message
    // box if the last prompt failed too (see RunTurnAsync). A hand-off is only a
    // real end of the running prompt (not "cancelled", not a failure) while others wait: the agent
    // has taken the next one in and carries on, so it stays one turn - what follows gets its own
    // assistant bubble below that message, and the turn stats keep running. A prompt dropped with its
    // session is no longer tracked.
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
        // One continuing turn: the timer and token count keep running from its start.
        _currentAssistantMessage = null;
        _submittedPrompts[0]?.Bubble.MarkSent();
    }

    // Sent ahead only to an agent that advertised it queues prompts itself: it takes them in at its
    // next input boundary, between the running turn's operations, without interrupting any of them.
    // Held back while a returned-unstarted message waits to go out again, so it keeps its place.
    private bool CanSendAhead => !_isStopping && _returnedUnstarted.Count == 0 && _connection?.SupportsPromptQueueing == true;

    // Auto-dispatch answers to the same admission gates as a message the user sends by hand: a queue
    // entry that outlives the state it was typed in is exactly the hazard CanQueueOrSendDraft
    // documents (a prompt landing in a session that is being torn down, swapped, signed out of, or
    // reconfigured). Every gate that can close here reopens by either discarding the queue with the
    // session it belonged to (DiscardQueuedMessages) or calling this again when it lifts - see the
    // config-change path - so a queued message is never stranded pending forever. RunTurnAsync sets
    // IsBusy before its first await, so after the first dispatch the rest go only as sends-ahead,
    // in queue order.
    private void DispatchNextQueuedMessage()
    {
        while (_queuedMessages.Count > 0 && CanQueueOrSendDraft && (!IsBusy || CanSendAhead))
            _ = DispatchQueuedMessageAsync(_queuedMessages.Dequeue());
    }

    // Called once nothing is running, for messages that must not be re-sent automatically (see
    // RunTurnAsync for a failed turn). The bubbles leave the transcript and
    // the texts go into the composer in the order they were sent, ahead of whatever is typed there.
    private void RestoreToComposer(List<QueuedMessage> pending, string one, string many)
    {
        if (pending.Count == 0) return;
        var restored = pending.ToList();
        pending.Clear();
        var texts = restored.Select(message => message.Text).Where(text => text.Length > 0).ToList();
        if (InputText.Trim().Length > 0) texts.Add(InputText);
        InputText = string.Join(Environment.NewLine + Environment.NewLine, texts);
        foreach (var message in restored)
        {
            Messages.Remove(message.Bubble);
            foreach (var attachment in message.Attachments)
                if (!Attachments.Contains(attachment)) Attachments.Add(attachment);
        }
        var notice = restored.Count == 1 ? one : string.Format(System.Globalization.CultureInfo.InvariantCulture, many, restored.Count);
        // Appended, not replacing: after a failed turn the error that caused this must stay readable.
        StatusMessage = string.IsNullOrEmpty(StatusMessage) ? notice : StatusMessage + " " + notice;
    }

    // Called once nothing is running: messages the agent returned unstarted go back to the front of
    // the queue, in the order they were sent, ahead of anything typed since. Returns whether any did.
    private bool RequeueReturnedUnstarted()
    {
        if (_returnedUnstarted.Count == 0) return false;
        var ordered = _returnedUnstarted.Concat(_queuedMessages).ToList();
        _returnedUnstarted.Clear();
        _queuedMessages.Clear();
        foreach (var message in ordered) _queuedMessages.Enqueue(message);
        return true;
    }

    /// <summary>Drops every message still waiting to go out - including any the agent was holding
    /// but had not started, or had returned unstarted - taking its bubble out of the transcript with
    /// it: the message never ran, so a bubble left behind would read as delivered. Returns how many
    /// were dropped so the caller can tell the user - they wrote them, and this is the only notice
    /// they will get.</summary>
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

    /// <summary>Adds the "queued messages were lost" clause to whatever the caller is already
    /// reporting, so neither the cause (disconnect, sign-out, workspace change) nor the
    /// consequence is dropped. Returns <paramref name="status"/> unchanged when nothing was lost.</summary>
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

    // The toast is one line of agent-authored text: the same normalization the session title gets,
    // at the notification's own bound, rather than a second half-rule that only knows about '\n'.
    private void RaiseAttention(ChatAttentionKind kind, string title, string message) =>
        AttentionRequested?.Invoke(this, new ChatAttentionEventArgs(kind, title, SessionTitleFormat.SingleLine(message, 160)));

    // Review comments are delivered as the next prompt as soon as the composer can send one. Every
    // transient blocker - the rejected plan's own turn, a config change, a document capture - calls
    // this again when it lifts, so comments held back here are never stranded.
    private void SendPendingPlanReview()
    {
        var comments = _pendingPlanReviewComments;
        if (comments is null || _disposed || !CanEditDraft || _isCapturingDocument) return;
        _pendingPlanReviewComments = null;
        var draft = InputText;
        InputText = "Review comments on the plan:\n" + comments;
        _ = SendReviewThenRestoreDraftAsync(draft);
    }

    // The composer stays live while the rejected plan's turn finishes, so the user can be mid-
    // sentence when the review goes out. The review is its own prompt, not a use of their draft:
    // put the draft back once the send has consumed the composer. Nothing here can throw -
    // SendCoreAsync swallows its own failures - so the fire-and-forget call site is safe.
    private async Task SendReviewThenRestoreDraftAsync(string draft)
    {
        await SendAsync().ConfigureAwait(true);
        // Still occupied means either the send never got as far as reading it (the review text is
        // still in there and is the more valuable of the two) or the user has typed again since.
        if (_disposed || draft.Length == 0 || InputText.Length > 0) return;
        InputText = draft;
    }

    public Task CancelAsync() => OnUiAsync(CancelCoreAsync);

    private async Task CancelCoreAsync()
    {
        if (_disposed || _connection is null || _sessionId is null) return;
        // Stop cancels the running turn, not the follow-ups the user has already written. Those still
        // held here stay queued; those the agent was holding come back "cancelled" and are queued
        // again ahead of them (OnPromptReturned). RunTurnAsync's tail sends them once every
        // cancelled prompt has returned.
        if (IsBusy) _isStopping = true;
        try
        {
            await _connection.CancelAsync(_sessionId, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            // The turn goes on, so sending ahead into it is safe again.
            _isStopping = false;
            if (!_disposed) StatusMessage = $"Cancel failed: {ex.Message}";
        }
    }

    public Task NewSessionAsync() => OnUiAsync(NewSessionCoreAsync);

    private async Task NewSessionCoreAsync()
    {
        if (!CanEditDraft) return;
        _isSwitchingSession = true;
        NotifyStateChanged();
        try
        {
            var sessionBeforeConnect = _sessionId;
            var (connection, sessionId) = await EnsureConnectedAsync(_lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            if (!string.Equals(sessionId, sessionBeforeConnect, StringComparison.Ordinal))
            {
                // EnsureConnectedAsync had to connect and has already created and adopted a fresh
                // session. A second session/new would orphan that one on the agent - and with
                // RemoteControlAtStartup the orphan can be the session published to claude.ai/code,
                // which the toggle then never reaches. Only the dead session's transcript is stale;
                // the catalog the adopt just applied belongs to the session we are keeping.
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
        StatusMessage = null;
        // Kept so the failure path below can put it all back: an id the agent never loaded would
        // keep routing every later prompt and config change to a session that does not exist, and
        // a transient session/load failure must not destroy the conversation the user was in. That
        // means everything ResetTranscriptState is about to wipe, not just the transcript - a
        // half-restored session lies twice over, with the messages intact beside an empty
        // changed-file panel and a context ring reading zero.
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
            // Known upfront (unlike session/new): set it before the call below so replayed
            // session/update notifications, tagged with this id, are not dropped by OnSessionUpdate's
            // "belongs to the known session" check while the request is still in flight.
            _sessionId = session.SessionId;
            // Never session.Cwd: it is copied verbatim out of the agent's session/list reply and
            // becomes the WorkspacePathGuard root of every VS-control tool for the resumed session.
            var result = await connection.LoadSessionAsync(session.SessionId, WorkspaceCwd, null, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(connection, _connection)) return;
            ApplyConfigOptions(result.ConfigOptions);
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
                // The agent's edits from this session are still on disk, so the rows that offer to
                // revert them have to come back with the transcript they belong to.
                lock (_changedFilesByPath)
                {
                    foreach (var file in changedFilesBeforeLoad) _changedFilesByPath[file.FullPath] = file;
                }

                foreach (var file in changedFilesBeforeLoad) ChangedFiles.Add(file);
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
        // Queue first: it is transcript-lifetime state like Messages, and going through the same
        // discard keeps "a pending bubble always has a live queue entry" true on every path.
        DiscardQueuedMessages();
        Messages.Clear();
        lock (_changedFilesByPath) _changedFilesByPath.Clear();
        _toolCallDiffsById.Clear();
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
        // The context ring and the turn-token delta belong to the session that is going away:
        // leaving them behind shows the previous conversation's usage over an empty transcript and
        // makes the next turn's TurnTokens a nonsense delta.
        _sessionUsedTokens = 0;
        _contextWindowSize = null;
        _turnStartUsedTokens = 0;
        TurnTokens = null;
        OnPropertyChanged(nameof(SessionUsedTokens));
        OnPropertyChanged(nameof(ContextWindowSize));
        OnPropertyChanged(nameof(ContextUsagePercent));
        OnPropertyChanged(nameof(ContextUsageLabel));
    }

    // Resolve, never abandon: an unresolved TaskCompletionSourceSlot leaves the agent's awaiter
    // hanging forever, and a form left on screen would answer a session that is already gone.
    private void ClearPendingRequests(string reason)
    {
        // Resolve the plan before dropping it: nothing else ever does, and a still-open plan
        // document would keep Proceed/Review live on a session that no longer exists.
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

    /// <summary>Issues <c>session/new</c> with the client's own trusted workspace root, buffering
    /// any command catalog the agent publishes while the new session's id is still unknown. Both
    /// call sites (the initial connect and New Chat) must pair this with
    /// <see cref="AdoptNewSession"/> and clear <c>_pendingCommandCatalogs</c> in their own finally.</summary>
    private Task<NewSessionResult> RequestNewSessionAsync(IAcpAgentConnection connection, CancellationToken cancellationToken)
    {
        _pendingCommandCatalogs = new Dictionary<string, IReadOnlyList<AvailableCommand>>(StringComparer.Ordinal);
        return connection.NewSessionAsync(WorkspaceCwd, null, cancellationToken);
    }

    /// <summary>Publishes the id <c>session/new</c> returned, drains the catalog buffered while it
    /// was unknown, and applies the settings the agent advertised for it.</summary>
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
                    // Replay only: a live turn already has the bubble SendCoreAsync added, so an
                    // agent that echoes the prompt back must not duplicate it (and must not be
                    // allowed to author the session title).
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
                    UpsertToolCall(toolCall.Call);
                    break;
                case SessionUpdate.Plan plan:
                    // Agents emit a plan update per todo transition; replacing the view model must
                    // not re-expand a Tasks list the user collapsed.
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
                    // Raised just before its prompt returns, so that prompt is still in
                    // _submittedPrompts. While another prompt is listed the turn goes on: either the
                    // agent has taken a queued message in and carries on working (hand-off - the
                    // reply so far is closed off so what follows lands below that message, but the
                    // turn is not finished and gets no stats yet), or Stop settled a prompt still
                    // waiting in its queue "cancelled" before the running one (nothing to show).
                    if (_submittedPrompts.Count > 1)
                    {
                        if (turnEnded.StopReason != "cancelled") _currentAssistantMessage = null;
                        break;
                    }
                    // IsBusy is owned by the running RunTurnAsync calls and clears only when the last
                    // of them returns, not here.
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
                    _toolCallDiffsById.Clear(); // every call of the turn has reported its final update by now
                    UpdateActivity("Working…");
                    _ = RefreshUsageAsync();
                    break;
            }
        });
    }

    // Subagent tool calls that have not finished, by id; see RunningAgentCount.
    private readonly HashSet<string> _runningSubagents = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>How many subagents (Claude Code's Agent tool) are running - the composer's
    /// "N agents" pill, like the VS Code extension's.</summary>
    public int RunningAgentCount => _runningSubagents.Count;

    /// <summary>The pill's text: "1 agent", "3 agents".</summary>
    public string RunningAgentsLabel => RunningAgentCount == 1 ? "1 agent" : RunningAgentCount + " agents";

    private void NotifyRunningAgents()
    {
        OnPropertyChanged(nameof(RunningAgentCount));
        OnPropertyChanged(nameof(RunningAgentsLabel));
    }

    // A dead agent, a stopped turn or a replaced transcript sends no final status for its subagents.
    private void ClearRunningSubagents()
    {
        if (_runningSubagents.Count == 0) return;
        _runningSubagents.Clear();
        NotifyRunningAgents();
    }

    private void UpsertToolCall(ToolCallUpdate call)
    {
        // File-system work (path canonicalization, whole-file reads) must never run on the WPF
        // dispatcher. Eligibility is captured here, on the UI thread: only a live turn produces
        // changes to revert - a resumed session replays old, already-applied tool calls whose
        // "original" would be the current file, giving 50 phantom rows with nothing to revert.
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
        // A later update can land on a new card (its bubble was closed off) without naming the tool.
        if (card.IsSubagent || _runningSubagents.Contains(card.ToolCallId))
        {
            bool changed = card.Status is ToolCallStatus.Completed or ToolCallStatus.Failed
                ? _runningSubagents.Remove(card.ToolCallId)
                : _runningSubagents.Add(card.ToolCallId);
            if (changed) NotifyRunningAgents();
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
            // The plan awaiting that request can no longer be answered: its document goes dead with it.
            if (_pendingPlan is { IsResolved: false }) _pendingPlan.MarkResolved("Superseded by a newer request");
            _pendingPermissionResponse = e.Response;
            PlanReviewViewModel? plan = null;
            void Choose(PermissionOption option)
            {
                // A surface outliving its request - a card answered twice, a plan document kept
                // open past a newer request - must not clear the state of whatever is pending now.
                if (!e.Response.TrySetResult(option.OptionId)) return;
                _pendingPermissionResponse = null;
                PendingPermission = null;
                // Answered from the card, the plan document has to go dead with it. The plan's own
                // callbacks mark it first, with their more specific status.
                if (plan is { IsResolved: false })
                {
                    plan.MarkResolved(option.Outcome is PermissionOutcome.AllowOnce or PermissionOutcome.AllowAlways
                        ? "Plan accepted — implementing…" : "Plan rejected");
                }
                UpdateActivity("Working…");
            }

            PendingPermission = new PermissionRequestViewModel(ToolDisplayName.Describe(e.Call.Title), e.Options, Choose);
            UpdateActivity("Waiting for permission…");

            // ExitPlanMode arrives as a switch_mode tool call whose content is the plan markdown.
            var planText = e.Call.Kind == "switch_mode"
                ? string.Join("\n", e.Call.Content.Where(content => !content.IsDiff && !string.IsNullOrWhiteSpace(content.Text)).Select(content => content.Text))
                : string.Empty;
            if (planText.Length > 0)
            {
                plan = new PlanReviewViewModel(planText, e.Options, Choose,
                    comments =>
                    {
                        // Unreachable, and only here to satisfy nullability: PlanReviewViewModel's
                        // ReviewCommand body itself returns before invoking this callback when
                        // RejectOption is null, so no host - panel, plan document, or a programmatic
                        // Execute that bypasses CanExecute - can get here with nothing to answer.
                        if (plan!.RejectOption is not PermissionOption reject) return;
                        _pendingPlanReviewComments = comments;
                        plan.MarkResolved("Sent back for revision");
                        Choose(reject);
                        // A locally driven turn owns IsBusy, so RunTurnAsync's tail delivers the
                        // review once the last in-flight prompt returns. A turn driven from claude.ai/code
                        // never sets it, and there is nothing to wait for: SessionUpdate.TurnEnded
                        // is raised from our own session/prompt response, so a remote turn produces
                        // none. Deferring on it would strand the user's typed review indefinitely,
                        // so it goes out now and the agent arbitrates the ordering.
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
                // A superseded form can still be on screen in a host surface; answering it must not
                // wipe the form the user is now looking at, whose slot nothing else would resolve.
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

    // Each entry keeps the terminator the document actually has after it. Choosing one terminator
    // for the whole slice from a whole-file scan rewrites the interior separators of a mixed-ending
    // document, and the agent then uses that text as the old_text of its follow-up Edit.
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

    // Only the terminator following the last requested line is dropped: a partial read must not
    // hand back a dangling "\r", and must not invent a terminator the file does not have there.
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
            // Separate leases: TrackChangeBeforeWriteAsync owns and releases its own lease for the
            // pre-write snapshot, so the write below re-acquires (and re-validates) the path.
            var (tracked, isNewRow) = await TrackChangeBeforeWriteAsync(e.Path).ConfigureAwait(true);
            if (isNewRow) created = tracked;
            using var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, e.Path);
            await WriteLeasedFileAsync(pathLease, e.Content).ConfigureAwait(true);
            RunOnUi(() => tracked.UpdateCounts(e.Content));
            e.Response.TrySetResult(true);
        }
        catch (Exception ex)
        {
            // The row went in before the write so the snapshot came first; a write that never
            // landed leaves nothing to list. A row an earlier, landed write created stays.
            if (created is not null) RunOnUi(() => UntrackChange(created));
            e.Response.TrySetException(ex);
        }
    }

    private readonly Dictionary<string, ChangedFileViewModel> _changedFilesByPath = new Dictionary<string, ChangedFileViewModel>(StringComparer.OrdinalIgnoreCase);

    // The latest diff content reported for each tool call still in flight. claude-agent-acp reports
    // an Edit/Write in three notifications (dist/acp-agent.js, dist/tools.js): the tool_call with the
    // model's optimistic diff, a PostToolUse-hook tool_call_update with the real structuredPatch
    // diff and no status (parsed as Pending), and a status=completed/failed tool_call_update whose
    // content is empty - toolUpdateFromToolResult returns {} for Edit and Write. The final update
    // therefore names nothing to count on its own; the diff to count is the last one it reported.
    // UI thread only (UpsertToolCall and ResetTranscriptState), where notifications are serialized.
    private readonly Dictionary<string, List<ToolCallContent>> _toolCallDiffsById = new Dictionary<string, List<ToolCallContent>>(StringComparer.Ordinal);

    // Resolves, in notification order, the diffs a tool-call notification should be tracked against:
    // its own when it carries any, otherwise - for the content-less final update - the last ones it
    // reported. Deciding this here rather than inside the per-notification background task keeps a
    // completed update from overtaking the hook update that carried its diff.
    private List<ToolCallContent> ResolveToolCallDiffs(ToolCallUpdate call)
    {
        var finished = call.Status is ToolCallStatus.Completed or ToolCallStatus.Failed;
        var diffs = call.Content.Where(content => content.IsDiff && !string.IsNullOrWhiteSpace(content.Path)).ToList();
        if (diffs.Count > 0) _toolCallDiffsById[call.ToolCallId] = diffs;
        else if (finished && _toolCallDiffsById.TryGetValue(call.ToolCallId, out var reported)) diffs = reported;
        if (finished) _toolCallDiffsById.Remove(call.ToolCallId);
        return diffs;
    }

    // The agent process writes Edit/Write results to disk itself (the client fs is not used for
    // them), so track those files from their tool-call diffs: snapshot the original while the call is
    // still pending (before the file changes), refresh the +/- counts once it completes.
    private async Task TrackToolCallFileChangesAsync(ToolCallUpdate call, List<ToolCallContent> diffs)
    {
        try
        {
            foreach (var content in diffs)
            {
                ChangedFileViewModel tracked;
                try { (tracked, _) = await TrackChangeBeforeWriteAsync(content.Path!, content, call.Status, call.ToolCallId).ConfigureAwait(true); }
                catch (Exception) { continue; } // outside the workspace or unreadable: not ours to revert.

                if (call.Status is not (ToolCallStatus.Completed or ToolCallStatus.Failed)) continue;
                string? current;
                using (var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, tracked.FullPath))
                    current = await ReadLeasedFileAsync(pathLease).ConfigureAwait(true);
                // The snapshot is mutable and is corrected from a thread-pool thread under this
                // lock (TrackChangeBeforeWriteAsync, whose insert-time comment says why a second
                // notification for this call is in flight at all), so read it under that lock too.
                string? snapshot;
                lock (_changedFilesByPath) snapshot = tracked.OriginalText;
                var unchanged = string.Equals(current, snapshot, StringComparison.Ordinal);
                RunOnUi(() =>
                {
                    if (call.Status == ToolCallStatus.Failed)
                    {
                        // Denied, or old_string not found (claude-agent-acp reports both as failed):
                        // the agent changed nothing, so there is nothing to count, revert, or list.
                        // A file it did change earlier in the session keeps its row.
                        if (unchanged) UntrackChange(tracked);
                        return;
                    }
                    tracked.UpdateCounts(current ?? string.Empty);
                    // The call reports a change yet the file still equals the snapshot: the snapshot
                    // was taken after the edit landed (or nothing changed). Either way a revert would
                    // only write the file over itself and report success.
                    if (unchanged) tracked.MarkNotRevertable();
                });
            }
        }
        catch (Exception)
        {
            // Change tracking is presentation only; it must never break the transcript.
        }
    }

    // Snapshot the pre-edit content on the agent's first write to a path, so Reject can restore it.
    // Also reports whether this call created the row, so a write that then fails can take it back.
    private async Task<(ChangedFileViewModel Entry, bool Created)> TrackChangeBeforeWriteAsync(
        string requestedPath, ToolCallContent? diff = null, ToolCallStatus status = ToolCallStatus.Completed, string? toolCallId = null)
    {
        using var pathLease = WorkspacePathGuard.AcquireFile(_services.WorkspaceRoot, requestedPath);
        lock (_changedFilesByPath)
        {
            if (_changedFilesByPath.TryGetValue(pathLease.FullPath, out var existing))
            {
                // The row's snapshot came from this call's earlier - pending - notification, which
                // can itself already have raced the agent's write and pinned a wrong snapshot (see
                // TryGetRaceCorrectedSnapshot). This is the only remaining chance to correct it: a
                // later notification for the same path returns this same row without re-reading the
                // file. Only the creating call may do so: a later call that writes the file back to
                // its original satisfies the same equality, and would replace a correct original
                // with the agent's intermediate content. Applied synchronously so the caller's
                // revertability check reads the corrected snapshot, not the one it replaces.
                if (toolCallId is not null && existing.CreatedByToolCallId == toolCallId
                    && TryGetRaceCorrectedSnapshot(existing.OriginalText, status, diff, out var corrected))
                {
                    existing.CorrectOriginalSnapshot(corrected);
                    // The agent reports "" as the pre-write content of a file it created (an empty
                    // structured patch, src/diff.ts: oldText = originalFile), and RevertChangeAsync
                    // tells an empty original from an absent one to choose between writing and
                    // deleting: restoring "" would truncate the file the agent created to zero
                    // bytes instead of removing it, and report success. Only the client's own read
                    // can prove a file existed and was empty; this text is the agent's word.
                    // The verdict is flipped here, in the same breath and under the same lock as
                    // the snapshot it condemns: a Reject that read the two apart passed the stale
                    // "yes" and wrote that very "" into the file. Only the notification is posted -
                    // this row may already be in the panel, and CanRevert drives a CanExecute.
                    if (corrected.Length == 0 && existing.TryMarkNotRevertable()) RunOnUi(existing.NotifyRevertabilityChanged);
                }

                return (existing, false);
            }
        }

        // Whether the agent created this file is decided here and only here, by the client's own
        // guarded read: a row is "new" (and so Reject deletes it) exactly when the read found no
        // file. An absent oldText is not evidence of creation - it is agent-controlled and adapters
        // omit it routinely - and inferring creation from it made Reject delete pre-existing files
        // whose content already matched the reported newText.
        var original = await ReadLeasedFileAsync(pathLease).ConfigureAwait(true);
        var raceCorrected = TryGetRaceCorrectedSnapshot(original, status, diff, out var correctedOriginal);
        if (raceCorrected) original = correctedOriginal;

        var entry = new ChangedFileViewModel(pathLease.FullPath, original,
            file => OnUiAsync(() => AcceptChangeAsync(file)),
            file => OnUiAsync(() => RejectChangeAsync(file)),
            toolCallId);
        // An Edit's diff is the model's old_string/new_string (src/tools.ts) and never the whole
        // file, so a post-edit snapshot cannot be recognised by equality - it is recognised by what
        // it lacks, the text the edit replaced. Such a row offers no revert: writing the snapshot
        // back would only rewrite the edit over itself and report success.
        if (diff is not null && !IsPreEditSnapshot(original, diff)) entry.MarkNotRevertable();
        // An empty corrected original is not restorable either, for the reason the entry-time
        // lookup above states. Called directly: this row is not in the panel yet.
        if (raceCorrected && original is { Length: 0 }) entry.MarkNotRevertable();
        lock (_changedFilesByPath)
        {
            if (_changedFilesByPath.TryGetValue(pathLease.FullPath, out var raced))
            {
                // Another notification for this same call inserted the row while the read above was
                // in flight - UpsertToolCall runs one task per notification, so a call's pending and
                // completed updates overlap - and its snapshot can have raced the agent's write just
                // as an earlier notification's can. This return discards the entry just built from
                // the corrected read, so it is the last chance to correct the row that survives:
                // same guard, and the same reason, as the entry-time lookup above.
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
            // Posted, so a session switch can have cleared the ledger in between; a row it no
            // longer knows would sit in the new session's panel with nothing behind it.
            bool live;
            lock (_changedFilesByPath) live = _changedFilesByPath.TryGetValue(entry.FullPath, out var current) && ReferenceEquals(current, entry);
            if (live) ChangedFiles.Add(entry);
        });
        return (entry, true);
    }

    // The notification carrying a diff is not synchronized with the agent's own write, so a snapshot
    // - whether just read from disk or already sitting on a tracked row - can equal the post-edit
    // content instead of the true original. When a completed call's diff carries the whole file
    // (claude-agent-acp's Write update with an empty structured patch, src/diff.ts: oldText =
    // originalFile), its OldText is the authoritative pre-write content to restore. Only a completed
    // call: on a pending or failed one the diff is the model's own input, and adopting it would let a
    // denied Edit choose what Reject writes into the file. It only ever corrects the content, never
    // the existence.
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

    // The file's line endings are its own and the diff's are the model's, so every comparison
    // between the two folds both. Comparing them raw - as the correction above once did - never
    // matched for a CRLF file, the norm in a Visual Studio workspace: the correction no-opped and
    // the row kept the snapshot that had raced the write, counting "+0 -0".
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

    // AsyncRelayCommand rethrows a faulted task onto the captured (UI) context, and the host's open
    // genuinely fails for a file renamed or deleted after it was tracked - Reject deletes files.
    private async Task OpenChangedFileAsync(ChangedFileViewModel? file)
    {
        if (file is null) return;
        var workspaceRoot = _services.WorkspaceRoot;
        try
        {
            // Re-validate rather than trust a path captured when the row was created: this is the
            // one consumer of a tracked path that did not, and the lease also pins the ancestor
            // chain for the duration of the open. Off the dispatcher, as everywhere else here.
            await Task.Run(async () =>
            {
                using var pathLease = WorkspacePathGuard.AcquireFile(workspaceRoot, file.FullPath);
                // Only a document lease makes FullPath safe for a path-based host API: the same pin
                // the read and write paths hold, held here across the open.
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
    /// Opens a file reference the user clicked in the transcript. <paramref name="href"/> crosses
    /// the WebView2 boundary carrying a path this renderer lifted out of untrusted agent markdown,
    /// so it is re-parsed and re-validated here rather than trusted: only a link this renderer
    /// emitted is accepted, and the path is confined to the workspace before the host ever sees it.
    /// Never faults - a reference to a file that was never there is ordinary agent output, not an
    /// error the chat should break on.
    /// </summary>
    public async Task OpenFileReferenceAsync(string? href)
    {
        if (!ChatFileReference.TryParseLink(href, out var reference, out var line)) return;

        try
        {
            // Inside the try on purpose: in the VSIX this property is a live callback into
            // solution state, which throws while a solution is closing or reloading. The only
            // caller discards this task on the WebView2 callback thread, so a fault here would be
            // an unobserved exception rather than the message the summary above promises.
            var workspaceRoot = _services.WorkspaceRoot;
            if (string.IsNullOrEmpty(workspaceRoot))
                throw new InvalidOperationException("no folder or solution is open.");

            // The agent writes workspace-relative paths, but WorkspacePathGuard resolves a relative
            // candidate against the process working directory - devenv's, which has nothing to do
            // with the workspace. Anchor it first so the guard judges the path the user meant.
            var candidate = Path.IsPathRooted(reference) ? reference : Path.Combine(workspaceRoot!, reference);
            await Task.Run(async () =>
            {
                using var pathLease = WorkspacePathGuard.AcquireFile(workspaceRoot, candidate);
                using var document = pathLease.ProtectDocument();
                if (document is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    throw new FileNotFoundException("the file does not exist.", pathLease.FullPath);
                await _services.OpenDocumentAsync(pathLease.FullPath, line, _lifetime.Token).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!_disposed) StatusMessage = $"Could not open {reference}: {ex.Message}";
        }
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

    // Every per-file failure would overwrite the previous one's status message, leaving the user
    // with one filename and the (correct) rows of the others still in the panel. Report once.
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
        // The snapshot and the verdict on it are corrected together, from a thread-pool thread,
        // under this lock (TrackChangeBeforeWriteAsync), so capture the pair under that same lock:
        // reading CanRevert outside it let a Reject pass the stale "yes" and then write back the
        // empty snapshot the correction had just installed, truncating the file the agent created
        // to zero bytes. Captured and released before the work starts - the lock is never held
        // across an await.
        bool canRevert;
        string? original;
        lock (_changedFilesByPath)
        {
            canRevert = file.CanRevert;
            original = file.OriginalText;
        }

        if (!canRevert) throw new InvalidOperationException("The content from before the edit is not known.");
        // Same rule as UpsertToolCall: lease acquisition (path canonicalization plus a chain of
        // directory-handle opens), File.Delete and WriteAllText are all synchronous, and Reject
        // all runs them once per file in a row straight off a click. Only UntrackChange, which
        // touches the observable collection, stays on the dispatcher.
        var workspaceRoot = _services.WorkspaceRoot;
        await Task.Run(async () =>
        {
            // The lease is held across the whole operation: releasing it to delete by path unpins
            // the ancestor chain and re-opens the reparse-point swap the lease type prevents.
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
            // Set first, release second: ReleaseConnectionAsync appends what the dropped queue costs
            // to this message instead of either of them overwriting the other.
            StatusMessage = ex is not null ? $"Agent disconnected: {ex.Message}" : "Agent disconnected.";
            _ = ReleaseConnectionAsync();
        });
    }

    private async Task ReleaseConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        _sessionId = null;
        // A queued message belongs to the session it was typed into. Releasing that session is the
        // one chokepoint every way of losing it goes through - disconnect, sign-out, workspace
        // switch, a failed connect - so the queue dies here rather than surviving to be delivered
        // into whatever session comes next. Callers set their own status first; this appends to it.
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
            // EnsureConnectedAsync's finally tolerates a disposed gate (ObjectDisposedException is
            // swallowed there), so a late in-flight continuation racing this disposal cannot throw unhandled.
            _connectGate.Dispose();
            _lifetime.Dispose();
        }
    }
}
