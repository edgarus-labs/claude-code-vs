using ClaudeCode.Contracts;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.VsControl.Mcp;

public sealed class VsControlPipeClient : IAsyncDisposable
{
    private const string _handshakeTokenEnvironmentVariable = "CLAUDECODE_VSCONTROL_TOKEN";
    private const string _buildSolutionMethod = "buildSolution";
    private const string _buildProjectMethod = "buildProject";
    private const string _startDebuggingMethod = "startDebugging";
    private const string _openSolutionMethod = "openSolution";
    private const string _addProjectToSolutionMethod = "addProjectToSolution";

    /// <summary>
    /// Default budget for the methods that compile or load a solution. Nothing cancels the Visual
    /// Studio side when a budget expires, so it must exceed the worst case the host can reach under it
    /// and let the agent receive the host's own actionable error rather than a transport timeout
    /// invented while MSBuild or a solution load is still running. The longest bounded case is
    /// <c>startDebugging</c>: a build bounded at 10 minutes by the host's own <c>RunBuildAsync</c>
    /// backstop, then up to 60 s for the launch to leave design mode
    /// (<c>VsControlPipeServer.Debugger.cs</c> <c>_startDebuggingTimeoutMs</c>), then up to 45 s for a
    /// breakpoint (<c>_maxWaitMs</c>, mirrored as the <c>waitForBreakMs</c> schema maximum):
    /// 600 + 60 + 45 = 705 s. Raising any of those host bounds requires raising this budget to stay
    /// above the sum; the Vsix has no test project, so nothing on this side can detect that drift.
    /// <c>openSolution</c>/<c>addProjectToSolution</c> share the budget without being bounded at all -
    /// their COM calls offer no completion signal to cancel against - so for those it is a ceiling
    /// rather than a proof.
    /// </summary>
    public static readonly TimeSpan DefaultLongOperationTimeout = TimeSpan.FromMinutes(12);

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _buildTimeout;
    private readonly string _handshakeToken;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VsControlResponse>> _pending = new();

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readLoopTask;
    private int _disposed;

    public VsControlPipeClient(
        string pipeName,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? buildTimeout = null,
        string? handshakeToken = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name must not be empty.", nameof(pipeName));
        }

        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        _buildTimeout = buildTimeout ?? DefaultLongOperationTimeout;
        _handshakeToken = handshakeToken ?? Environment.GetEnvironmentVariable(_handshakeTokenEnvironmentVariable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_handshakeToken) || _handshakeToken.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("A nonempty single-line VS control handshake token is required.", nameof(handshakeToken));
        }
        _lifetimeToken = _lifetimeSource.Token;
    }

    public async Task<VsControlResponse> SendAsync(VsControlRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        CancellationToken operationToken = operationSource.Token;

        await EnsureConnectedAsync(operationToken).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<VsControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, tcs))
        {
            throw new InvalidOperationException($"A pending VS control request with id '{request.Id}' already exists.");
        }

        try
        {
            string line = JsonSerializer.Serialize(request, _jsonOptions);

            await _writeLock.WaitAsync(operationToken).ConfigureAwait(false);
            try
            {
                operationToken.ThrowIfCancellationRequested();
                var writer = _writer ?? throw new InvalidOperationException("VS control pipe is not connected.");
                var transport = writer.BaseStream;
                try
                {
                    await writer.WriteLineAsync(line.AsMemory(), operationToken).ConfigureAwait(false);
                    await writer.FlushAsync(operationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A failed write may have sent an incomplete JSON line. Retire exactly this
                    // writer's transport so a retry cannot append a request to that partial frame.
                    DisposeQuietly(transport);
                    throw;
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            _pending.TryRemove(request.Id, out _);

            throw;
        }

        var timeout = UsesLongOperationBudget(request.Method) ? _buildTimeout : _requestTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        await using var timeoutRegistration = timeoutCts.Token.Register(static state => ((TaskCompletionSource<VsControlResponse>)state!).TrySetCanceled(), tcs);
        await using var registration = operationToken.Register(static state => ((TaskCompletionSource<VsControlResponse>)state!).TrySetCanceled(), tcs);
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!operationToken.IsCancellationRequested)
        {
            // Neither caller nor disposal cancellation: the per-request timeout fired instead.
            // Surface this as a normal VsControlResponse error, never an unhandled exception - a slow
            // or wedged VS host must not hang the MCP tool call indefinitely.
            return new VsControlResponse
            {
                Id = request.Id,
                Error = $"Timed out waiting for a response to '{request.Method}' after {timeout.TotalSeconds:0.#}s.",
            };
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    // The methods whose server-side cost cannot fit the 60 s per-request budget.
    // buildSolution/buildProject run MSBuild; startDebugging builds the solution before it launches;
    // openSolution and addProjectToSolution drive DTE.Solution.Close/Open/AddFromFile, synchronous
    // uncancellable COM calls that the host cannot bound at all - there is no completion signal to
    // race a CancellationToken against, so abandoning the wait would only let a retry close and
    // reopen the solution a second time. Every method that does have a server-side bound is bounded
    // below _requestTimeout: 45 s mode waits, 60 s debug launch wait, 15 s stop, 5 s evaluations,
    // 5 s UI-automation actions.
    private static bool UsesLongOperationBudget(string method)
        => method is _buildSolutionMethod
            or _buildProjectMethod
            or _startDebuggingMethod
            or _openSolutionMethod
            or _addProjectToSolutionMethod;

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pipe is { IsConnected: true })
            {
                return;
            }

            // Finish the old reader before a new connection can own pending requests.
            if (_readLoopTask is not null)
            {
                await _readLoopTask.ConfigureAwait(false);
            }
            DisposeConnectionState();

            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            StreamReader? reader = null;
            StreamWriter? writer = null;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_connectTimeout);
            try
            {
                await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
                reader = new StreamReader(pipe, _utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                writer = new StreamWriter(pipe, _utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };

                // Keep this connection private until the entire token has been sent. Neither a
                // concurrent caller nor a retry may send requests through a failed handshake.
                await writer.WriteLineAsync(_handshakeToken.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                await writer.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                _pipe = pipe;
                _reader = reader;
                _writer = writer;
                _readLoopTask = ReadLoopAsync(pipe, reader);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DisposeQuietly(pipe);
                DisposeQuietly(writer);
                DisposeQuietly(reader);
                throw new TimeoutException(
                    $"Timed out connecting to VS control pipe '{_pipeName}' after {_connectTimeout.TotalSeconds:0.#}s. " +
                    "Is the ClaudeCode Visual Studio extension host running for this session?");
            }
            catch
            {
                // Close the transport before disposing a buffered writer: teardown must not flush
                // a partial token into an unresponsive peer or leave a connected field behind.
                DisposeQuietly(pipe);
                DisposeQuietly(writer);
                DisposeQuietly(reader);
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, StreamReader reader)
    {
        Exception? failure = null;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                VsControlResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<VsControlResponse>(line, _jsonOptions);
                }
                catch (JsonException)
                {
                    // Malformed line from the VS host: skip it rather than tearing down the connection.
                    continue;
                }

                if (response is not null && _pending.TryRemove(response.Id, out var tcs))
                {
                    tcs.TrySetResult(response);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        failure ??= new IOException("VS control pipe closed unexpectedly.");
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetException(failure);
            }
        }

        if (!ReferenceEquals(_pipe, pipe))
        {
            return;
        }

        // Tear down under the write gate. Disposing _writer while SendAsync is still inside
        // WriteLineAsync/FlushAsync makes StreamWriter.Dispose throw InvalidOperationException
        // ("the stream is currently in use by a previous operation"), which escapes DisposeQuietly's
        // filter, faults this task and resurfaces from DisposeAsync's await. DisposeAsync cancels
        // the lifetime before it takes the same gate and keeps it through disposal, so a canceled
        // lifetime means disposal already owns teardown: stand down instead of waiting on a gate
        // that is never released.
        try
        {
            await _writeLock.WaitAsync(_lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (ReferenceEquals(_pipe, pipe))
            {
                DisposeConnectionState();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            // The remote end may have already closed the pipe (e.g. after sending its final
            // response); StreamWriter/StreamReader/PipeStream.Dispose can try to flush against
            // that closed pipe and throw IOException or, once the underlying PipeStream has
            // already torn itself down, ObjectDisposedException. This is a normal teardown race
            // between the read loop noticing disconnection and an explicit DisposeAsync call, not
            // a real failure.
        }
    }

    private void DisposeConnectionState()
    {
        DisposeQuietly(_pipe);
        DisposeQuietly(_writer);
        DisposeQuietly(_reader);
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeSource.Cancel();
        // Acquiring both gates joins any current handshake and write. Keep ownership through
        // disposal, so canceled waiters cannot acquire a gate and later release it after disposal.
        await _connectLock.WaitAsync().ConfigureAwait(false);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        var readLoop = _readLoopTask;
        DisposeConnectionState();

        if (readLoop is not null)
        {
            await readLoop.ConfigureAwait(false);
        }

        _connectLock.Dispose();
        _writeLock.Dispose();
        _lifetimeSource.Dispose();
    }
}
