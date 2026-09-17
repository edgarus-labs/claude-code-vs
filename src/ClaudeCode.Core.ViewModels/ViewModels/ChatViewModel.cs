using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCode.Core.ViewModels
{
    /// <summary>
    /// Owns one chat session: message history, composer state, and the lazily-created ACP connection.
    /// Never connects at construction time - the child process is only spawned on the first send, so simply
    /// opening the tool window has no side effects.
    /// </summary>
    public sealed class ChatViewModel : ObservableObject, IDisposable
    {
        private readonly IChatSessionServices _services;
        private readonly SynchronizationContext? _uiContext;
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);

        private IAcpAgentConnection? _connection;
        private string? _sessionId;
        private ChatMessageViewModel? _currentAssistantMessage;
        private bool _disposed;

        public ChatViewModel(IChatSessionServices services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _uiContext = SynchronizationContext.Current;

            SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
            CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsBusy);
            SignInCommand = new AsyncRelayCommand(SignInAsync, () => !IsSignedIn);

            _services.AuthService.StateChanged += OnAuthStateChanged;
            ApplyAuthState(_services.AuthService.CurrentState);

            // "On construction (or first activation)": kick off the async, authoritative sign-in check now.
            // Errors surface via StatusMessage rather than throwing out of the constructor.
            _ = InitializeAsync();
        }

        public ObservableCollection<ChatMessageViewModel> Messages { get; } = new ObservableCollection<ChatMessageViewModel>();

        private string _composerText = string.Empty;

        public string ComposerText
        {
            get => _composerText;
            set => SetProperty(ref _composerText, value);
        }

        private bool _isBusy;

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        private bool _isSignedIn;

        public bool IsSignedIn
        {
            get => _isSignedIn;
            private set
            {
                if (SetProperty(ref _isSignedIn, value))
                {
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        private string? _statusMessage;

        public string? StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        private PlanViewModel? _currentPlan;

        public PlanViewModel? CurrentPlan
        {
            get => _currentPlan;
            private set => SetProperty(ref _currentPlan, value);
        }

        private PermissionRequestViewModel? _pendingPermission;

        public PermissionRequestViewModel? PendingPermission
        {
            get => _pendingPermission;
            private set => SetProperty(ref _pendingPermission, value);
        }

        public IAsyncRelayCommand SendCommand { get; }

        public IAsyncRelayCommand CancelCommand { get; }

        public IAsyncRelayCommand SignInCommand { get; }

        /// <summary>Refreshes <see cref="IsSignedIn"/> from the authoritative async check. Safe to call repeatedly.</summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var signedIn = await _services.AuthService.IsSignedInAsync(cancellationToken).ConfigureAwait(true);
                IsSignedIn = signedIn;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not check sign-in state: {ex.Message}";
            }
        }

        public async Task SignInAsync()
        {
            StatusMessage = "Signing in to Claude...";
            var progress = new Progress<string>(message => RunOnUi(() => StatusMessage = message));
            try
            {
                await _services.AuthService.SignInAsync(CancellationToken.None, progress).ConfigureAwait(true);
                IsSignedIn = await _services.AuthService.IsSignedInAsync(CancellationToken.None).ConfigureAwait(true);
                StatusMessage = IsSignedIn ? null : "Sign-in did not complete.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Sign-in failed: {ex.Message}";
            }
        }

        private bool CanSend() => IsSignedIn && !IsBusy;

        public async Task SendAsync()
        {
            var text = ComposerText?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            ComposerText = string.Empty;
            Messages.Add(new ChatMessageViewModel(ChatRole.User, text!));
            _currentAssistantMessage = null;
            IsBusy = true;

            try
            {
                var (connection, sessionId) = await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(true);
                var content = new ContentBlock[] { new ContentBlock.Text(text!) };
                await connection.SendPromptAsync(sessionId, content, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                // Normally cleared by the TurnEnded SessionUpdate; this is a safety net for failures where
                // the agent never gets a chance to report one.
                IsBusy = false;
            }
        }

        public async Task CancelAsync()
        {
            if (_connection == null || _sessionId == null)
            {
                return;
            }

            try
            {
                await _connection.CancelAsync(_sessionId, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Cancel failed: {ex.Message}";
            }
        }

        private async Task<(IAcpAgentConnection connection, string sessionId)> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_connection != null && _sessionId != null)
            {
                return (_connection, _sessionId);
            }

            await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (_connection != null && _sessionId != null)
                {
                    return (_connection, _sessionId);
                }

                var connection = await _services.ConnectionFactory.ConnectAsync(cancellationToken).ConfigureAwait(true);
                connection.SessionUpdate += OnSessionUpdate;
                connection.PermissionRequested += OnPermissionRequested;
                connection.FileReadRequested += OnFileReadRequested;
                connection.FileWriteRequested += OnFileWriteRequested;
                connection.Disconnected += OnDisconnected;

                await connection.InitializeAsync(cancellationToken).ConfigureAwait(true);
                var workspaceRoot = _services.WorkspaceRoot ?? Environment.CurrentDirectory;
                var sessionId = await connection.NewSessionAsync(workspaceRoot, null, cancellationToken).ConfigureAwait(true);

                _connection = connection;
                _sessionId = sessionId;
                return (connection, sessionId);
            }
            finally
            {
                _connectGate.Release();
            }
        }

        private void OnSessionUpdate(object? sender, SessionUpdateEventArgs e)
        {
            if (e.SessionId != _sessionId)
            {
                return;
            }

            RunOnUi(() =>
            {
                switch (e.Update)
                {
                    case SessionUpdate.AgentMessageChunk chunk:
                        EnsureAssistantMessage().AppendText(chunk.Text);
                        break;

                    case SessionUpdate.AgentThoughtChunk thought:
                        EnsureAssistantMessage().AppendText(thought.Text);
                        break;

                    case SessionUpdate.ToolCall toolCall:
                        UpsertToolCall(toolCall.Call);
                        break;

                    case SessionUpdate.Plan plan:
                        CurrentPlan = new PlanViewModel(plan.Entries);
                        break;

                    case SessionUpdate.TurnEnded:
                        IsBusy = false;
                        _currentAssistantMessage = null;
                        break;
                }
            });
        }

        private void UpsertToolCall(ToolCallUpdate call)
        {
            var message = EnsureAssistantMessage();
            var existing = message.ToolCalls.FirstOrDefault(t => t.ToolCallId == call.ToolCallId);
            if (existing != null)
            {
                existing.Apply(call);
            }
            else
            {
                message.ToolCalls.Add(new ToolCallCardViewModel(call));
            }
        }

        private ChatMessageViewModel EnsureAssistantMessage()
        {
            if (_currentAssistantMessage == null)
            {
                _currentAssistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
                Messages.Add(_currentAssistantMessage);
            }

            return _currentAssistantMessage;
        }

        private void OnPermissionRequested(object? sender, PermissionRequestEventArgs e)
        {
            if (e.SessionId != _sessionId)
            {
                return;
            }

            RunOnUi(() =>
            {
                PendingPermission = new PermissionRequestViewModel(e.Call.Title, e.Options, option =>
                {
                    e.Response.SetResult(option.OptionId);
                    PendingPermission = null;
                });
            });
        }

        /// <summary>
        /// Default fs/read_text_file handler: reads straight off disk via System.IO. The Vsix host may want
        /// to override this (by wrapping/replacing the connection) to route through live, unsaved editor
        /// buffers instead - this default is enough to make the pipeline work end-to-end today.
        /// </summary>
        private void OnFileReadRequested(object? sender, FileReadRequestEventArgs e)
        {
            try
            {
                var text = File.ReadAllText(e.Path);
                if (e.Line.HasValue || e.Limit.HasValue)
                {
                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    var start = Math.Max(0, (e.Line ?? 1) - 1);
                    var count = e.Limit ?? Math.Max(0, lines.Length - start);
                    text = string.Join("\n", lines.Skip(start).Take(count));
                }

                e.Response.SetResult(text);
            }
            catch (Exception ex)
            {
                e.Response.SetException(ex);
            }
        }

        /// <summary>Default fs/write_text_file handler: writes straight to disk via System.IO. See remarks on <see cref="OnFileReadRequested"/>.</summary>
        private void OnFileWriteRequested(object? sender, FileWriteRequestEventArgs e)
        {
            try
            {
                File.WriteAllText(e.Path, e.Content);
                e.Response.SetResult(true);
            }
            catch (Exception ex)
            {
                e.Response.SetException(ex);
            }
        }

        private void OnDisconnected(object? sender, Exception? ex)
        {
            RunOnUi(() =>
            {
                IsBusy = false;
                StatusMessage = ex != null ? $"Agent disconnected: {ex.Message}" : "Agent disconnected.";
            });
        }

        private void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e)
        {
            RunOnUi(() => ApplyAuthState(e.State, e.Detail));
        }

        private void ApplyAuthState(AuthState state, string? detail = null)
        {
            IsSignedIn = state == AuthState.SignedIn;
            if (detail != null)
            {
                StatusMessage = detail;
            }
        }

        private void NotifyCommandsCanExecuteChanged()
        {
            SendCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            SignInCommand.NotifyCanExecuteChanged();
        }

        private void RunOnUi(Action action)
        {
            if (_uiContext != null && _uiContext != SynchronizationContext.Current)
            {
                _uiContext.Post(_ => action(), null);
            }
            else
            {
                action();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _services.AuthService.StateChanged -= OnAuthStateChanged;

            if (_connection != null)
            {
                _connection.SessionUpdate -= OnSessionUpdate;
                _connection.PermissionRequested -= OnPermissionRequested;
                _connection.FileReadRequested -= OnFileReadRequested;
                _connection.FileWriteRequested -= OnFileWriteRequested;
                _connection.Disconnected -= OnDisconnected;
                _ = _connection.DisposeAsync();
            }

            _connectGate.Dispose();
        }
    }
}
