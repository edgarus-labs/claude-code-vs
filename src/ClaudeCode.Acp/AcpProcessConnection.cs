using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

public sealed partial class AcpProcessConnection : IAcpAgentConnection
{
    private const int _protocolVersion = 1;

    private static readonly TimeSpan _gracefulShutdownTimeout = TimeSpan.FromSeconds(3);

    // The connection is already known to be broken when SetSessionConfigOptionAsync's error path
    // disposes it - there is no well-behaved agent left to wait politely for, so use a much shorter
    // grace period before moving on to killing the process.
    private static readonly TimeSpan _errorPathShutdownTimeout = TimeSpan.FromMilliseconds(200);

    public const string CancelledPermissionOptionId = "__acp_cancelled__";

    private readonly JsonRpcConnection _rpc;
    private readonly Process? _process;
    private readonly WindowsJobProcess? _windowsProcess;
    private readonly Task? _stderrPump;

    private readonly object _permissionGate = new object();
    private readonly Dictionary<string, HashSet<PermissionRequestEventArgs>> _pendingPermissionsBySession =
        new Dictionary<string, HashSet<PermissionRequestEventArgs>>();
    private Exception? _permissionFailure;

    private readonly object _elicitationGate = new object();
    private readonly Dictionary<string, HashSet<ElicitationRequestEventArgs>> _pendingElicitationsBySession =
        new Dictionary<string, HashSet<ElicitationRequestEventArgs>>();
    private Exception? _elicitationFailure;

    private int _disposed;
    private int _disconnected;
    private int _isInitialized;
    private volatile bool _supportsPromptQueueing;

    public AcpProcessConnection(
        string executableFileName,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        if (string.IsNullOrWhiteSpace(executableFileName))
        {
            throw new ArgumentException("An executable path or command name is required.", nameof(executableFileName));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executableFileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (arguments is not null && arguments.Count > 0)
        {
            startInfo.Arguments = ProcessArgumentEscaping.ToArgumentsString(arguments);
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (environmentVariables is not null)
        {
            foreach (var pair in environmentVariables)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _windowsProcess = WindowsJobProcess.Start(startInfo);
            _process = _windowsProcess.Process;
            try
            {
                _rpc = new JsonRpcConnection(_windowsProcess.StandardOutput, _windowsProcess.StandardInput);
                WireRpcHandlers();
                _rpc.Start();
                _stderrPump = PumpStandardErrorAsync(_windowsProcess.StandardError);
            }
            catch
            {
                _windowsProcess.Dispose();
                throw;
            }
        }
        else
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.ErrorDataReceived += OnStandardErrorReceived;
            try
            {
                _process.Start();
                _process.BeginErrorReadLine();
                _rpc = new JsonRpcConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
                WireRpcHandlers();
                _rpc.Start();
            }
            catch
            {
                _process.ErrorDataReceived -= OnStandardErrorReceived;
                try
                {
                    TerminateProcess(_process);
                }
                finally
                {
                    _process.Dispose();
                }

                throw;
            }
        }
    }

    internal AcpProcessConnection(Stream readFrom, Stream writeTo)
    {
        _process = null;
        _rpc = new JsonRpcConnection(readFrom, writeTo);
        WireRpcHandlers();
        _rpc.Start();
    }

    private void OnStandardErrorReceived(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null)
        {
            StandardErrorReceived?.Invoke(this, args.Data);
        }
    }

    private async Task PumpStandardErrorAsync(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            StandardErrorReceived?.Invoke(this, line);
        }
    }

    public bool IsInitialized => Volatile.Read(ref _isInitialized) != 0;

    public bool SupportsPromptQueueing => _supportsPromptQueueing;

    public event EventHandler<SessionUpdateEventArgs>? SessionUpdate;

    public event EventHandler<PermissionRequestEventArgs>? PermissionRequested;

    public event EventHandler<ElicitationRequestEventArgs>? ElicitationRequested;

    public event EventHandler<FileReadRequestEventArgs>? FileReadRequested;

    public event EventHandler<FileWriteRequestEventArgs>? FileWriteRequested;

    public event EventHandler<Exception?>? Disconnected;

    public event EventHandler<string>? StandardErrorReceived;

    private void WireRpcHandlers()
    {
        _rpc.RequestHandler = HandleInboundRequestAsync;
        _rpc.NotificationReceived += (_, notification) => OnNotificationReceived(notification);
        _rpc.Disconnected += (_, ex) => ReportDisconnected(ex);
    }

    private void ReportDisconnected(Exception? error)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _isInitialized, 0);
        var disconnectCause = error ?? new IOException("The ACP agent connection was closed.");
        FailAllPendingPermissions(disconnectCause);
        FailAllPendingElicitations(disconnectCause);
        if (Volatile.Read(ref _disposed) != 0)
        {
            // DisposeAsync() already initiated this shutdown intentionally - the consumer asked for
            // it and does not need an unsolicited "the connection was lost" notification too.
            return;
        }

        Disconnected?.Invoke(this, error);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsInitialized)
        {
            // The connection factory already performs the ACP `initialize` handshake before
            // handing out a connection; a second call (e.g. from a caller that also initializes
            // defensively) must not re-send `initialize` over the wire, since a conforming agent
            // may reject or reset session state on a repeated handshake.
            return;
        }

        var @params = new JsonObject
        {
            ["protocolVersion"] = _protocolVersion,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = true, ["writeTextFile"] = true },
                ["terminal"] = false,
                // Form support only - url-mode elicitation (directing the user to an external page)
                // is not implemented.
                ["elicitation"] = new JsonObject { ["form"] = new JsonObject() },
            },
        };

        JsonNode? result = await _rpc.SendRequestAsync("initialize", @params, cancellationToken).ConfigureAwait(false);
        _supportsPromptQueueing = ReadsPromptQueueing(result);
        Volatile.Write(ref _isInitialized, 1);
    }

    // claude-agent-acp's extension marker (agentCapabilities._meta.claudeCode.promptQueueing): a
    // session/prompt sent while one is running is queued by the agent and taken up at its next input
    // boundary. Agent-supplied, so only a literal JSON true counts.
    private static bool ReadsPromptQueueing(JsonNode? result) =>
        result is JsonObject response
        && response["agentCapabilities"] is JsonObject capabilities
        && capabilities["_meta"] is JsonObject meta
        && meta["claudeCode"] is JsonObject claudeCode
        && claudeCode["promptQueueing"] is JsonValue flag
        && flag.GetValueKind() == System.Text.Json.JsonValueKind.True;

    /// <summary>
    /// Starts a new ACP session rooted at <paramref name="cwd"/>. <paramref name="cwd"/> is sent to
    /// the remote agent process as-is - this class does not validate, canonicalize, or sandbox it in
    /// any way. The caller MUST pass only a path it already trusts (e.g. one already checked against
    /// a workspace boundary); this class has no way to distinguish an intentionally-opened workspace
    /// from an attacker-controlled path.
    /// </summary>
    public async Task<NewSessionResult> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
    {
        var @params = new JsonObject
        {
            ["cwd"] = cwd,
            // ACP marks `mcpServers` required on NewSessionRequest (possibly empty); always send an array
            // even when the caller passed null/empty, rather than the task-literal "only if non-empty".
            ["mcpServers"] = BuildMcpServersArray(mcpServers),
        };

        JsonNode? result = await _rpc.SendRequestAsync("session/new", @params, cancellationToken).ConfigureAwait(false);
        var obj = result as JsonObject ?? throw new AcpProtocolException("session/new response did not contain a result object.");

        // A repeated key anywhere in the response body throws ArgumentException when the object is
        // first materialized, before any field can be read; report it as a malformed response.
        string sessionId;
        try
        {
            sessionId = GetRequiredString(obj, "sessionId");
        }
        catch (ArgumentException)
        {
            throw new AcpProtocolException("session/new response did not contain a valid 'sessionId'.");
        }

        return new NewSessionResult(sessionId, ParseConfigOptions(obj));
    }

    // Extension request answered by the Visual Studio launcher script (claude-acp-vs.mjs), not by the
    // stock adapter; the launcher forwards it to the Agent SDK's enableRemoteControl control request.
    public async Task<RemoteControlState> SetRemoteControlAsync(string sessionId, bool enabled, string? name, CancellationToken cancellationToken)
    {
        var @params = new JsonObject { ["sessionId"] = sessionId, ["enabled"] = enabled };
        if (!string.IsNullOrWhiteSpace(name)) @params["name"] = name;

        JsonNode? result = await _rpc.SendRequestAsync("_vs/remoteControl", @params, cancellationToken).ConfigureAwait(false);
        var obj = result as JsonObject ?? throw new AcpProtocolException("_vs/remoteControl response did not contain a result object.");
        // The agent is authoritative about whether the toggle took effect. Falling back to the
        // requested value would leave the UI claiming the session is (or is no longer) exposed at
        // claude.ai/code on the word of an agent that never acknowledged it.
        if (obj["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out bool resultEnabled))
        {
            throw new AcpProtocolException("_vs/remoteControl response did not report the resulting 'enabled' state.");
        }

        return new RemoteControlState(resultEnabled, GetOptionalString(obj, "sessionUrl"));
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken)
    {
        var @params = new JsonObject { ["cwd"] = cwd };
        JsonNode? result = await _rpc.SendRequestAsync("session/list", @params, cancellationToken).ConfigureAwait(false);
        var obj = result as JsonObject ?? throw new AcpProtocolException("session/list response did not contain a result object.");
        return ParseSessionSummaries(obj);
    }

    public async Task<NewSessionResult> LoadSessionAsync(string sessionId, string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
    {
        var @params = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["cwd"] = cwd,
            // ACP marks `mcpServers` required on LoadSessionRequest (possibly empty); always send an
            // array, mirroring NewSessionAsync's convention above.
            ["mcpServers"] = BuildMcpServersArray(mcpServers),
        };

        JsonNode? result = await _rpc.SendRequestAsync("session/load", @params, cancellationToken).ConfigureAwait(false);
        var obj = result as JsonObject ?? throw new AcpProtocolException("session/load response did not contain a result object.");
        return new NewSessionResult(sessionId, ParseConfigOptions(obj));
    }

    public async Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var @params = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["configId"] = configId,
            ["value"] = value,
        };

        try
        {
            JsonNode? result = await _rpc.SendRequestAsync("session/set_config_option", @params, cancellationToken).ConfigureAwait(false);
            var obj = result as JsonObject ?? throw new AcpProtocolException("session/set_config_option response did not contain a result object.");
            return ParseConfigOptions(obj, required: true);
        }
        catch (Exception ex) when (ex is not AcpRemoteException)
        {
            // Without an authoritative acknowledgement, the agent may already have applied the
            // value. Do not let callers keep sending with an apparently rolled-back setting.
            try
            {
                ReportDisconnected(ex);
            }
            finally
            {
                await DisposeAsync(_errorPathShutdownTimeout).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task<string> SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
    {
        var promptArray = new JsonArray();
        foreach (ContentBlock block in content)
        {
            promptArray.Add(ToWireContentBlock(block));
        }

        var @params = new JsonObject { ["sessionId"] = sessionId, ["prompt"] = promptArray };

        // Design note: ACP v1's `session/prompt` request *is* the end-of-turn signal - the RPC call only
        // resolves once the whole turn (streaming chunks, tool calls, permission round-trips) has fully
        // finished, and its response carries the terminal `stopReason`
        // (https://agentclientprotocol.com/protocol/v1/schema#session-prompt). There is no separate
        // "turn ended" `session/update` notification in the v1 schema, so `SessionUpdate.TurnEnded` is
        // raised here, from the response, immediately before returning - not from a notification handler.
        JsonNode? result = await _rpc.SendRequestAsync("session/prompt", @params, cancellationToken).ConfigureAwait(false);
        string stopReason = result is JsonObject obj ? GetOptionalString(obj, "stopReason") ?? "end_turn" : "end_turn";
        SessionUpdate?.Invoke(this, new SessionUpdateEventArgs(sessionId, new SessionUpdate.TurnEnded(stopReason)));
        return stopReason;
    }

    public async Task CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        // session/cancel is a notification (fire-and-forget); the in-flight session/prompt call for this
        // session resolves later with stopReason "cancelled" once the agent has wound down.
        try
        {
            await _rpc.SendNotificationAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Per spec, once session/cancel has been sent the client MUST answer any outstanding
            // session/request_permission calls for this session with RequestPermissionOutcome::Cancelled, even
            // if the UI never gets around to answering the prompt itself. Resolve them proactively.
            // This also has to happen when the notification could not be written at all (already
            // cancelled token, dead transport) - that is exactly when an unanswered prompt would
            // otherwise hang in the UI forever.
            CancelPendingPermissions(sessionId);

            // Same reasoning for a still-open elicitation form (e.g. an unanswered AskUserQuestion): the
            // turn is winding down, so resolve it as cancelled rather than leaving the UI's response task
            // hanging forever.
            CancelPendingElicitations(sessionId);
        }
    }

    public ValueTask DisposeAsync() => DisposeAsync(_gracefulShutdownTimeout);

    internal async ValueTask DisposeAsync(TimeSpan gracefulShutdownTimeout)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        bool stderrTimedOut = false;
        try
        {
            _rpc.CloseOutput(); // close stdin: signals EOF so a well-behaved agent exits on its own.

            if (_process is not null)
            {
                await WaitForExitAsync(_process, gracefulShutdownTimeout).ConfigureAwait(false);
                if (_windowsProcess is null && !_process.HasExited)
                {
                    TerminateProcess(_process);
                }
            }
        }
        finally
        {
            // Closing the job also terminates descendants whose direct launcher already exited.
            _windowsProcess?.Terminate();
            try
            {
                await _rpc.DisposeAsync().ConfigureAwait(false);
                if (_stderrPump is not null)
                {
                    if (await Task.WhenAny(_stderrPump, Task.Delay(gracefulShutdownTimeout)).ConfigureAwait(false) != _stderrPump)
                    {
                        // A subscriber can block indefinitely. Cleanup must still finish; observe
                        // any later failure after reporting that the callback could not be drained.
                        _ = _stderrPump.ContinueWith(task => { _ = task.Exception; },
                            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        stderrTimedOut = true;
                    }
                    else
                    {
                        await _stderrPump.ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (_process is not null)
                {
                    _process.ErrorDataReceived -= OnStandardErrorReceived;
                }

                if (_windowsProcess is not null)
                {
                    _windowsProcess.Dispose();
                }
                else
                {
                    _process?.Dispose();
                }
            }
        }

        if (stderrTimedOut)
        {
            throw new TimeoutException("The ACP stderr subscriber did not finish during shutdown.");
        }
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => tcs.TrySetResult(true);
        process.Exited += handler;
        try
        {
            if (process.HasExited)
            {
                return;
            }

            await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        }
        finally
        {
            process.Exited -= handler;
        }
    }

    private static void TerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // There is no associated live process (including a failed start).
        }
        catch (Win32Exception) when (process.HasExited)
        {
            // The process exited between the check and the kill.
        }
    }

    private void TrackPendingPermission(string sessionId, PermissionRequestEventArgs args)
    {
        lock (_permissionGate)
        {
            if (_permissionFailure is not null)
            {
                args.Response.TrySetException(_permissionFailure);
                return;
            }

            if (!_pendingPermissionsBySession.TryGetValue(sessionId, out var bag))
            {
                bag = new HashSet<PermissionRequestEventArgs>();
                _pendingPermissionsBySession.Add(sessionId, bag);
            }

            bag.Add(args);
        }
    }

    private void UntrackPendingPermission(string sessionId, PermissionRequestEventArgs args)
    {
        lock (_permissionGate)
        {
            if (_pendingPermissionsBySession.TryGetValue(sessionId, out var bag))
            {
                bag.Remove(args);
                if (bag.Count == 0)
                {
                    _pendingPermissionsBySession.Remove(sessionId);
                }
            }
        }
    }

    private void CancelPendingPermissions(string sessionId)
    {
        lock (_permissionGate)
        {
            if (_pendingPermissionsBySession.TryGetValue(sessionId, out var bag))
            {
                foreach (var args in bag)
                {
                    args.Response.TrySetResult(CancelledPermissionOptionId);
                }
            }
        }
    }

    private void FailAllPendingPermissions(Exception cause)
    {
        lock (_permissionGate)
        {
            _permissionFailure = cause;
            foreach (var bag in _pendingPermissionsBySession.Values)
            {
                foreach (var args in bag)
                {
                    args.Response.TrySetException(cause);
                }
            }
        }
    }

    private void TrackPendingElicitation(string sessionId, ElicitationRequestEventArgs args)
    {
        lock (_elicitationGate)
        {
            if (_elicitationFailure is not null)
            {
                args.Response.TrySetException(_elicitationFailure);
                return;
            }

            if (!_pendingElicitationsBySession.TryGetValue(sessionId, out var bag))
            {
                bag = new HashSet<ElicitationRequestEventArgs>();
                _pendingElicitationsBySession.Add(sessionId, bag);
            }

            bag.Add(args);
        }
    }

    private void UntrackPendingElicitation(string sessionId, ElicitationRequestEventArgs args)
    {
        lock (_elicitationGate)
        {
            if (_pendingElicitationsBySession.TryGetValue(sessionId, out var bag))
            {
                bag.Remove(args);
                if (bag.Count == 0)
                {
                    _pendingElicitationsBySession.Remove(sessionId);
                }
            }
        }
    }

    private void CancelPendingElicitations(string sessionId)
    {
        lock (_elicitationGate)
        {
            if (_pendingElicitationsBySession.TryGetValue(sessionId, out var bag))
            {
                foreach (var args in bag)
                {
                    args.Response.TrySetResult(new ElicitationAnswer(ElicitationAction.Cancel, _emptyElicitationContent));
                }
            }
        }
    }

    private void FailAllPendingElicitations(Exception cause)
    {
        lock (_elicitationGate)
        {
            _elicitationFailure = cause;
            foreach (var bag in _pendingElicitationsBySession.Values)
            {
                foreach (var args in bag)
                {
                    args.Response.TrySetException(cause);
                }
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _emptyElicitationContent =
        new Dictionary<string, IReadOnlyList<string>>();
}
