using ClaudeCode.Contracts;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VsControlResponse>> _pending = new();

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readLoopTask;
    private bool _disposed;

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
    }

    public async Task<VsControlResponse> SendAsync(VsControlRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<VsControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, tcs))
        {
            throw new InvalidOperationException($"A pending VS control request with id '{request.Id}' already exists.");
        }

        try
        {
            string line = JsonSerializer.Serialize(request, _jsonOptions);
            var writer = _writer ?? throw new InvalidOperationException("VS control pipe is not connected.");

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        await using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<VsControlResponse>)state!).TrySetCanceled(), tcs);
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Not the caller's own cancellation: our internal per-request timeoutCts fired instead.
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
        if (_pipe is { IsConnected: true })
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe is { IsConnected: true })
            {
                return;
            }

            DisposeConnectionState();

            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_connectTimeout);
            try
            {
                await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                pipe.Dispose();

                throw new TimeoutException(
                    $"Timed out connecting to VS control pipe '{_pipeName}' after {_connectTimeout.TotalSeconds:0.#}s. " +
                    "Is the ClaudeCode Visual Studio extension host running for this session?");
            }
            catch
            {
                pipe.Dispose();

                throw;
            }

            _pipe = pipe;
            _reader = new StreamReader(pipe, _utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            _writer = new StreamWriter(pipe, _utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };

            // Handshake: the first line on every connection must be the shared token (see
            // VsControlSessionRegistry.StartSession / VsControlPipeServer.TryHandshakeAsync), written
            // before any MCP tool call is ever translated onto this pipe.
            await _writer.WriteLineAsync(_handshakeToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Intentionally CancellationToken.None: this read loop's lifetime is tied to the pipe
            // connection itself (see ReadLoopAsync's own disconnect handling and DisposeAsync),
            // not to EnsureConnectedAsync's connect-scoped cancellationToken, which may legitimately
            // be cancelled (e.g. a caller's per-request timeout) well before the connection - and
            // this background read loop - should end.
            _readLoopTask = Task.Run(() => ReadLoopAsync(pipe, _reader), CancellationToken.None);
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
        DisposeQuietly(_writer);
        DisposeQuietly(_reader);
        DisposeQuietly(_pipe);
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var readLoop = _readLoopTask;
        DisposeConnectionState();

        if (readLoop is not null)
        {
            try
            {
                await readLoop.ConfigureAwait(false);
            }
            catch
            {
                // Already surfaced to any pending callers via ReadLoopAsync's failure propagation.
            }
        }

        _connectLock.Dispose();
        _writeLock.Dispose();
    }
}
