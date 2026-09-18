using ClaudeCode.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<PermissionRequestEventArgs, byte>> _pendingPermissionsBySession =
        new ConcurrentDictionary<string, ConcurrentDictionary<PermissionRequestEventArgs, byte>>();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ElicitationRequestEventArgs, byte>> _pendingElicitationsBySession =
        new ConcurrentDictionary<string, ConcurrentDictionary<ElicitationRequestEventArgs, byte>>();

    private int _disposed;
    private int _disconnected;
    private int _isInitialized;

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

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                // Trace (not Debug - [Conditional("DEBUG")] compiles it out of Release builds) so
                // diagnostic stderr output is never silently lost when nothing has subscribed to
                // StandardErrorReceived.
                Trace.WriteLine("[ACP stderr] " + e.Data);
                StandardErrorReceived?.Invoke(this, e.Data);
            }
        };

        _process.Start();
        _process.BeginErrorReadLine();

        _rpc = new JsonRpcConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
        WireRpcHandlers();
        _rpc.Start();
    }

    internal AcpProcessConnection(Stream readFrom, Stream writeTo)
    {
        _process = null;
        _rpc = new JsonRpcConnection(readFrom, writeTo);
        WireRpcHandlers();
        _rpc.Start();
    }

    public bool IsInitialized => Volatile.Read(ref _isInitialized) != 0;

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

        await _rpc.SendRequestAsync("initialize", @params, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _isInitialized, 1);
    }

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

        var sessionId = GetRequiredString(obj, "sessionId");
        return new NewSessionResult(sessionId, ParseConfigOptions(obj));
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

    public async Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
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
    }

    public async Task CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        // session/cancel is a notification (fire-and-forget); the in-flight session/prompt call for this
        // session resolves later with stopReason "cancelled" once the agent has wound down.
        await _rpc.SendNotificationAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId }, cancellationToken).ConfigureAwait(false);

        // Per spec, once session/cancel has been sent the client MUST answer any outstanding
        // session/request_permission calls for this session with RequestPermissionOutcome::Cancelled, even
        // if the UI never gets around to answering the prompt itself. Resolve them proactively.
        CancelPendingPermissions(sessionId);

        // Same reasoning for a still-open elicitation form (e.g. an unanswered AskUserQuestion): the
        // turn is winding down, so resolve it as cancelled rather than leaving the UI's response task
        // hanging forever.
        CancelPendingElicitations(sessionId);
    }

    public ValueTask DisposeAsync() => DisposeAsync(_gracefulShutdownTimeout);

    internal async ValueTask DisposeAsync(TimeSpan gracefulShutdownTimeout)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _rpc.CloseOutput(); // close stdin: signals EOF so a well-behaved agent exits on its own.

            if (_process is not null)
            {
                await WaitForExitAsync(_process, gracefulShutdownTimeout).ConfigureAwait(false);
                if (!_process.HasExited)
                {
                    TerminateProcessTree(_process);
                }
            }
        }
        finally
        {
            await _rpc.DisposeAsync().ConfigureAwait(false);
            _process?.Dispose();
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

    private static void TerminateProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // .NET Standard 2.0 has no Process.Kill(entireProcessTree: true): target this process
                // (and any children it spawned, e.g. a launcher script execing a real runtime) via
                // taskkill instead of only killing the direct child and orphaning its descendants. Use
                // taskkill's full path to avoid resolving it against an attacker-influenced PATH.
                using var cleanup = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                    Arguments = "/PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (cleanup is not null && !cleanup.WaitForExit(2000))
                {
                    cleanup.Kill();
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
        {
            // The process may exit between the check and the targeted cleanup.
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                // already exited between the check and the call.
            }
        }
    }

    private void TrackPendingPermission(string sessionId, PermissionRequestEventArgs args)
    {
        var bag = _pendingPermissionsBySession.GetOrAdd(sessionId, _ => new ConcurrentDictionary<PermissionRequestEventArgs, byte>());
        bag[args] = 0;
    }

    private void UntrackPendingPermission(string sessionId, PermissionRequestEventArgs args)
    {
        if (_pendingPermissionsBySession.TryGetValue(sessionId, out var bag))
        {
            bag.TryRemove(args, out _);
            if (bag.IsEmpty)
            {
                // Compare-and-remove: only drop the session entry if it still holds this exact
                // (now-empty) bag instance, so a permission request that raced in concurrently and
                // installed a fresh bag for the same session id is never dropped.
                var entry = new KeyValuePair<string, ConcurrentDictionary<PermissionRequestEventArgs, byte>>(sessionId, bag);
                ((ICollection<KeyValuePair<string, ConcurrentDictionary<PermissionRequestEventArgs, byte>>>)_pendingPermissionsBySession).Remove(entry);
            }
        }
    }

    private void CancelPendingPermissions(string sessionId)
    {
        if (_pendingPermissionsBySession.TryGetValue(sessionId, out var bag))
        {
            foreach (var args in bag.Keys)
            {
                args.Response.TrySetResult(CancelledPermissionOptionId);
            }
        }
    }

    private void FailAllPendingPermissions(Exception cause)
    {
        foreach (var bag in _pendingPermissionsBySession.Values)
        {
            foreach (var args in bag.Keys)
            {
                args.Response.TrySetException(cause);
            }
        }
    }

    private void TrackPendingElicitation(string sessionId, ElicitationRequestEventArgs args)
    {
        var bag = _pendingElicitationsBySession.GetOrAdd(sessionId, _ => new ConcurrentDictionary<ElicitationRequestEventArgs, byte>());
        bag[args] = 0;
    }

    private void UntrackPendingElicitation(string sessionId, ElicitationRequestEventArgs args)
    {
        if (_pendingElicitationsBySession.TryGetValue(sessionId, out var bag))
        {
            bag.TryRemove(args, out _);
            if (bag.IsEmpty)
            {
                // Compare-and-remove, mirroring UntrackPendingPermission above.
                var entry = new KeyValuePair<string, ConcurrentDictionary<ElicitationRequestEventArgs, byte>>(sessionId, bag);
                ((ICollection<KeyValuePair<string, ConcurrentDictionary<ElicitationRequestEventArgs, byte>>>)_pendingElicitationsBySession).Remove(entry);
            }
        }
    }

    private void CancelPendingElicitations(string sessionId)
    {
        if (_pendingElicitationsBySession.TryGetValue(sessionId, out var bag))
        {
            foreach (var args in bag.Keys)
            {
                args.Response.TrySetResult(new ElicitationAnswer(ElicitationAction.Cancel, _emptyElicitationContent));
            }
        }
    }

    private void FailAllPendingElicitations(Exception cause)
    {
        foreach (var bag in _pendingElicitationsBySession.Values)
        {
            foreach (var args in bag.Keys)
            {
                args.Response.TrySetException(cause);
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _emptyElicitationContent =
        new Dictionary<string, IReadOnlyList<string>>();
}
