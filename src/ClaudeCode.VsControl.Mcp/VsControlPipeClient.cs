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
        _buildTimeout = buildTimeout ?? TimeSpan.FromMinutes(5);
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

        var timeout = string.Equals(request.Method, _buildSolutionMethod, StringComparison.Ordinal) ? _buildTimeout : _requestTimeout;
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

        if (ReferenceEquals(_pipe, pipe))
        {
            DisposeConnectionState();
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
