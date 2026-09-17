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
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VsControlResponse>> _pending = new();

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readLoopTask;
    private bool _disposed;

    public VsControlPipeClient(string pipeName, TimeSpan? connectTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name must not be empty.", nameof(pipeName));
        }

        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
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
                await writer.FlushAsync().ConfigureAwait(false);
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

        await using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<VsControlResponse>)state!).TrySetCanceled(), tcs);
        try
        {
            return await tcs.Task.ConfigureAwait(false);
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

            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
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
            _readLoopTask = Task.Run(() => ReadLoopAsync(pipe, _reader));
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
        catch (IOException)
        {
            // The remote end may have already closed the pipe (e.g. after sending its final
            // response); StreamWriter/StreamReader/PipeStream.Dispose can try to flush against
            // that closed pipe and throw. This is a normal teardown race, not a real failure.
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
