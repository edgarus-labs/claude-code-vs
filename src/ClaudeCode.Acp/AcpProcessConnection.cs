using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;

namespace ClaudeCode.Acp
{
    /// <summary>
    /// Live ACP connection to a spawned `claude-code-acp` (or compatible) child process. Owns the process
    /// lifetime, wires a <see cref="JsonRpcConnection"/> to its stdin/stdout, and translates the ACP wire
    /// protocol (see the ACP schema: https://agentclientprotocol.com/protocol/v1/schema) to/from the
    /// protocol-agnostic types in ClaudeCode.Contracts.
    /// </summary>
    public sealed partial class AcpProcessConnection : IAcpAgentConnection
    {
        /// <summary>The latest ACP protocol major version this client speaks.</summary>
        private const int ProtocolVersion = 1;

        private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Sentinel value completed onto a pending <see cref="PermissionRequestEventArgs.Response"/> to signal
        /// ACP's <c>RequestPermissionOutcome::Cancelled</c> outcome back over the wire. The contract's
        /// <see cref="TaskCompletionSourceSlot{T}"/> for permission responses is a bare optionId string with no
        /// dedicated "cancelled" slot, so this connection resolves any still-pending permission requests for a
        /// session with this sentinel itself - both when <see cref="CancelAsync"/> is called (per spec: the
        /// client MUST answer outstanding `session/request_permission` calls with Cancelled once
        /// `session/cancel` has been sent) and when the underlying process disconnects mid-prompt.
        /// </summary>
        public const string CancelledPermissionOptionId = "__acp_cancelled__";

        private readonly JsonRpcConnection _rpc;
        private readonly Process? _process;
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<PermissionRequestEventArgs, byte>> _pendingPermissionsBySession =
            new ConcurrentDictionary<string, ConcurrentDictionary<PermissionRequestEventArgs, byte>>();
        private int _disposed;

        /// <summary>Spawns the agent as a child process and wires a JSON-RPC transport to its stdio. Does not
        /// call `initialize` - call <see cref="InitializeAsync"/> (or go through <see cref="AcpProcessConnectionFactory"/>,
        /// which does this for you) before using the connection.</summary>
        /// <param name="executableFileName">Absolute path or bare command name (resolved via PATH) of the agent executable.</param>
        /// <param name="arguments">Extra command-line arguments (e.g. `--acp` for the `claude` CLI fallback).</param>
        /// <param name="workingDirectory">Working directory for the process itself; independent of the ACP session `cwd`.</param>
        /// <param name="environmentVariables">Extra/overriding environment variables merged on top of the
        /// process's normally-inherited environment (e.g. CLAUDE_CODE_OAUTH_TOKEN) - never replaces it.</param>
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

            if (arguments != null && arguments.Count > 0)
            {
                startInfo.Arguments = ProcessArgumentEscaping.ToArgumentsString(arguments);
            }

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            if (environmentVariables != null)
            {
                foreach (var pair in environmentVariables)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    StandardErrorReceived?.Invoke(this, e.Data);
                }
            };

            _process.Start();
            _process.BeginErrorReadLine();

            _rpc = new JsonRpcConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
            WireRpcHandlers();
            _rpc.Start();
        }

        /// <summary>Test-only entry point: wires the ACP protocol layer directly to caller-supplied duplex
        /// streams (e.g. in-memory pipes) without spawning a real process.</summary>
        internal AcpProcessConnection(Stream readFrom, Stream writeTo)
        {
            _process = null;
            _rpc = new JsonRpcConnection(readFrom, writeTo);
            WireRpcHandlers();
            _rpc.Start();
        }

        public bool IsInitialized { get; private set; }

        public event EventHandler<SessionUpdateEventArgs>? SessionUpdate;

        public event EventHandler<PermissionRequestEventArgs>? PermissionRequested;

        public event EventHandler<FileReadRequestEventArgs>? FileReadRequested;

        public event EventHandler<FileWriteRequestEventArgs>? FileWriteRequested;

        public event EventHandler<Exception?>? Disconnected;

        /// <summary>Raw stderr lines from the child process, surfaced for diagnostics/logging. Not part of
        /// <see cref="IAcpAgentConnection"/> since it has no ACP-protocol meaning.</summary>
        public event EventHandler<string>? StandardErrorReceived;

        private void WireRpcHandlers()
        {
            _rpc.RequestHandler = HandleInboundRequestAsync;
            _rpc.NotificationReceived += (_, notification) => OnNotificationReceived(notification);
            _rpc.Disconnected += (_, ex) =>
            {
                FailAllPendingPermissions(ex ?? new IOException("The ACP agent connection was closed."));
                Disconnected?.Invoke(this, ex);
            };
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            var @params = new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["clientCapabilities"] = new JsonObject
                {
                    ["fs"] = new JsonObject { ["readTextFile"] = true, ["writeTextFile"] = true },
                    ["terminal"] = false,
                },
            };

            await _rpc.SendRequestAsync("initialize", @params, cancellationToken).ConfigureAwait(false);
            IsInitialized = true;
        }

        public async Task<string> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
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
            return GetRequiredString(obj, "sessionId");
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
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _rpc.CloseOutput(); // close stdin: signals EOF so a well-behaved agent exits on its own.

                if (_process != null)
                {
                    await WaitForExitAsync(_process, GracefulShutdownTimeout).ConfigureAwait(false);
                    if (!_process.HasExited)
                    {
                        try
                        {
                            _process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                            // already exited between the check and the call.
                        }
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
            }
        }

        private void CancelPendingPermissions(string sessionId)
        {
            if (_pendingPermissionsBySession.TryGetValue(sessionId, out var bag))
            {
                foreach (var args in bag.Keys)
                {
                    args.Response.SetResult(CancelledPermissionOptionId);
                }
            }
        }

        private void FailAllPendingPermissions(Exception cause)
        {
            foreach (var bag in _pendingPermissionsBySession.Values)
            {
                foreach (var args in bag.Keys)
                {
                    args.Response.SetException(cause);
                }
            }
        }
    }
}
