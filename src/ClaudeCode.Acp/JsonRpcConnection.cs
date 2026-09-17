using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>>();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private long _nextId;
    private Task? _pumpTask;
    private int _disposed;

    public JsonRpcConnection(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public JsonRpcRequestHandler? RequestHandler { get; set; }

    public event EventHandler<JsonRpcNotification>? NotificationReceived;

    public event EventHandler<Exception?>? Disconnected;

    public void Start()
    {
        if (_pumpTask is not null)
        {
            throw new InvalidOperationException("JsonRpcConnection.Start() has already been called.");
        }

        _pumpTask = Task.Run(PumpAsync);
    }

    public async Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            var envelope = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params ?? new JsonObject(),
            };

            try
            {
                await WriteMessageAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _pending.TryRemove(id, out _);
                tcs.TrySetException(ex);
            }

            return await tcs.Task.ConfigureAwait(false);
        }
    }

    public Task SendNotificationAsync(string method, JsonNode? @params, CancellationToken cancellationToken)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params ?? new JsonObject(),
        };

        return WriteMessageAsync(envelope, cancellationToken);
    }

    public void CloseOutput()
    {
        try
        {
            _output.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task WriteMessageAsync(JsonObject envelope, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString() + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PumpAsync()
    {
        Exception? failure = null;
        try
        {
            // Manual byte-buffered line reads (rather than StreamReader.ReadLineAsync, which has no
            // CancellationToken overload on netstandard2.0) so DisposeAsync can reliably unblock a pending
            // read via _cts regardless of the underlying Stream implementation - a disposed/closed stream
            // does not reliably unblock an in-flight read on every Stream type (e.g. System.IO.Pipelines'
            // PipeReader-backed streams used in tests), whereas cancelling the token does.
            var readBuffer = new byte[8192];
            var pendingLine = new List<byte>(256);
            while (true)
            {
                int read = await _input.ReadAsync(readBuffer, 0, readBuffer.Length, _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break; // clean EOF: peer closed its stdout (process exited).
                }

                for (int i = 0; i < read; i++)
                {
                    byte b = readBuffer[i];
                    if (b == (byte)'\n')
                    {
                        if (pendingLine.Count > 0)
                        {
                            DispatchLine(Encoding.UTF8.GetString(pendingLine.ToArray()));
                            pendingLine.Clear();
                        }
                    }
                    else if (b != (byte)'\r')
                    {
                        pendingLine.Add(b);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Intentional shutdown via DisposeAsync(); not a connection failure.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            FailAllPending(failure ?? new IOException("The JSON-RPC connection was closed."));
            Disconnected?.Invoke(this, failure);
        }
    }

    private void DispatchLine(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (Exception)
        {
            return; // malformed line on the wire; nothing sane to do but drop it and keep reading.
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        obj.TryGetPropertyValue("method", out var methodNode);
        obj.TryGetPropertyValue("id", out var idNode);
        string? method = methodNode is JsonValue methodValue && methodValue.TryGetValue<string>(out var methodStr) ? methodStr : null;
        bool hasId = idNode is not null;

        if (method is not null)
        {
            obj.TryGetPropertyValue("params", out var paramsNode);
            if (hasId)
            {
                // Dispatched off the pump so a slow (e.g. user-permission) handler never blocks reading
                // subsequent notifications/responses that arrive while it's in flight.
                JsonNode? idNodeCapture = idNode;
                string methodCapture = method;
                JsonNode? paramsCapture = paramsNode;
                _ = Task.Run(() => HandleInboundRequestAsync(idNodeCapture, methodCapture, paramsCapture));
            }
            else
            {
                NotificationReceived?.Invoke(this, new JsonRpcNotification(method, paramsNode));
            }

            return;
        }

        if (!hasId)
        {
            return; // neither a request/notification (no method) nor a response (no id): not a message we understand.
        }

        if (!TryGetCorrelationId(idNode, out long requestId) || !_pending.TryRemove(requestId, out var tcs))
        {
            return; // response to an id we never sent (or already completed via cancellation) - ignore.
        }

        if (obj.TryGetPropertyValue("error", out var errorNode) && errorNode is JsonObject errorObj)
        {
            string message = errorObj.TryGetPropertyValue("message", out var m) && m is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "JSON-RPC error";
            int code = errorObj.TryGetPropertyValue("code", out var c) && c is JsonValue cv && cv.TryGetValue<int>(out var ci) ? ci : 0;
            errorObj.TryGetPropertyValue("data", out var dataNode);
            tcs.TrySetException(new AcpRemoteException(code, message, dataNode));
        }
        else
        {
            obj.TryGetPropertyValue("result", out var resultNode);
            tcs.TrySetResult(resultNode);
        }
    }

    private async Task HandleInboundRequestAsync(JsonNode? idNode, string method, JsonNode? @params)
    {
        WireId id;
        try
        {
            id = WireId.FromNode(idNode);
        }
        catch (Exception)
        {
            return; // id wasn't a number or string; nothing we can correlate a response to.
        }

        var handler = RequestHandler;
        try
        {
            if (handler is null)
            {
                await WriteErrorResponseAsync(id, -32601, $"Method not found: {method}").ConfigureAwait(false);

                return;
            }

            JsonNode? result = await handler(method, @params, _cts.Token).ConfigureAwait(false);
            await WriteResultResponseAsync(id, result).ConfigureAwait(false);
        }
        catch (AcpRemoteException remoteEx)
        {
            try
            {
                await WriteErrorResponseAsync(id, remoteEx.Code, remoteEx.Message).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Connection is gone; Disconnected has already fired (or is about to) from the pump.
            }
        }
        catch (Exception ex)
        {
            try
            {
                await WriteErrorResponseAsync(id, -32603, ex.Message).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private Task WriteResultResponseAsync(WireId id, JsonNode? result)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.ToNode(),
            ["result"] = result ?? new JsonObject(),
        };

        return WriteMessageAsync(envelope, CancellationToken.None);
    }

    private Task WriteErrorResponseAsync(WireId id, int code, string message)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.ToNode(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

        return WriteMessageAsync(envelope, CancellationToken.None);
    }

    private static bool TryGetCorrelationId(JsonNode? idNode, out long id)
    {
        id = 0;
        if (idNode is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<long>(out id))
        {
            return true;
        }

        if (value.TryGetValue<double>(out double asDouble))
        {
            id = (long)asDouble;

            return true;
        }

        return value.TryGetValue<string>(out var asString) && long.TryParse(asString, out id);
    }

    private void FailAllPending(Exception cause)
    {
        foreach (long key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(cause);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CloseOutput(); // best-effort: signal EOF to the remote peer's stdin.
        _cts.Cancel(); // unblocks the pump's pending read via the CancellationToken passed to ReadAsync.

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        try
        {
            _input.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _writeLock.Dispose();
        _cts.Dispose();
    }

    private readonly struct WireId
    {
        private readonly long _numeric;
        private readonly string? _text;

        private WireId(long numeric, string? text)
        {
            _numeric = numeric;
            _text = text;
        }

        public static WireId FromNode(JsonNode? node)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<long>(out long asLong))
                {
                    return new WireId(asLong, null);
                }

                if (value.TryGetValue<double>(out double asDouble))
                {
                    return new WireId((long)asDouble, null);
                }

                if (value.TryGetValue<string>(out var asString))
                {
                    return new WireId(0, asString);
                }
            }

            throw new AcpProtocolException("JSON-RPC request id must be a number or a string.");
        }

        public JsonNode ToNode() => _text is not null ? JsonValue.Create(_text)! : JsonValue.Create(_numeric)!;
    }
}
