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
    private const int _maxLineLengthBytes = 32 * 1024 * 1024;

    private static readonly TimeSpan _disposeGracePeriod = TimeSpan.FromSeconds(5);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _inboundRequestThrottle = new SemaphoreSlim(16, 16);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>>();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly CancellationToken _shutdownToken;
    private readonly object _operationGate = new object();
    private int _activeOperations = 1;
    private long _nextId;
    private Task? _pumpTask;
    private int _disposed;

    public JsonRpcConnection(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _shutdownToken = _cts.Token;
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

        BeginOperation();
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
        BeginOperation();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString() + "\n");

            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
            CancellationToken linkedToken = linkedCts.Token;
            await _writeLock.WaitAsync(linkedToken).ConfigureAwait(false);
            try
            {
                await _output.WriteAsync(bytes, 0, bytes.Length, linkedToken).ConfigureAwait(false);
                await _output.FlushAsync(linkedToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task PumpAsync()
    {
        Exception? failure = null;
        try
        {
            var readBuffer = new byte[8192];
            var pendingLine = new List<byte>(256);
            while (true)
            {
                int read = await _input.ReadAsync(readBuffer, 0, readBuffer.Length, _shutdownToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
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
                        if (pendingLine.Count > _maxLineLengthBytes)
                        {
                            pendingLine.Clear();
                            _cts.Cancel();
                            throw new IOException(
                                $"A single JSON-RPC line exceeded the {_maxLineLengthBytes}-byte limit without a newline; the connection has been closed.");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            try
            {
                FailAllPending(failure ?? new IOException("The JSON-RPC connection was closed."));
                Disconnected?.Invoke(this, failure);
            }
            finally
            {
                EndOperation();
            }
        }
    }

    private void DispatchLine(string line)
    {
        JsonObject obj;
        JsonNode? methodNode;
        JsonNode? idNode;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject parsed)
            {
                return;
            }

            obj = parsed;
            obj.TryGetPropertyValue("method", out methodNode);
            obj.TryGetPropertyValue("id", out idNode);
        }
        catch (Exception)
        {
            return;
        }

        string? method = methodNode is JsonValue methodValue && methodValue.TryGetValue<string>(out var methodStr) ? methodStr : null;
        bool hasId = idNode is not null;

        if (method is not null)
        {
            obj.TryGetPropertyValue("params", out var paramsNode);
            if (hasId)
            {
                JsonNode? idNodeCapture = idNode;
                string methodCapture = method;
                JsonNode? paramsCapture = paramsNode;
                _ = Task.Run(() => HandleInboundRequestThrottledAsync(idNodeCapture, methodCapture, paramsCapture));
            }
            else
            {
                NotificationReceived?.Invoke(this, new JsonRpcNotification(method, paramsNode));
            }

            return;
        }

        if (!hasId)
        {
            return;
        }

        if (!TryGetCorrelationId(idNode, out long requestId) || !_pending.TryRemove(requestId, out var tcs))
        {
            return;
        }

        if (obj.TryGetPropertyValue("error", out var errorNode) && errorNode is JsonObject errorObj)
        {
            tcs.TrySetException(ReadRemoteError(errorObj));
        }
        else
        {
            obj.TryGetPropertyValue("result", out var resultNode);
            tcs.TrySetResult(resultNode);
        }
    }

    private static AcpRemoteException ReadRemoteError(JsonObject errorObj)
    {
        try
        {
            string message = errorObj.TryGetPropertyValue("message", out var m) && m is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "JSON-RPC error";
            int code = errorObj.TryGetPropertyValue("code", out var c) && c is JsonValue cv && cv.TryGetValue<int>(out var ci) ? ci : 0;
            errorObj.TryGetPropertyValue("data", out var dataNode);
            return new AcpRemoteException(code, message, dataNode);
        }
        catch (Exception)
        {
            return new AcpRemoteException(0, "JSON-RPC error");
        }
    }

    internal async Task HandleInboundRequestThrottledAsync(JsonNode? idNode, string method, JsonNode? @params)
    {
        BeginOperation();
        try
        {
            await _inboundRequestThrottle.WaitAsync(_shutdownToken).ConfigureAwait(false);
            try
            {
                await HandleInboundRequestAsync(idNode, method, @params).ConfigureAwait(false);
            }
            finally
            {
                _inboundRequestThrottle.Release();
            }
        }
        finally
        {
            EndOperation();
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
            return;
        }

        var handler = RequestHandler;
        try
        {
            if (handler is null)
            {
                await WriteErrorResponseAsync(id, -32601, $"Method not found: {method}").ConfigureAwait(false);

                return;
            }

            JsonNode? result = await handler(method, @params, _shutdownToken).ConfigureAwait(false);
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
            }
        }
        catch (Exception)
        {
            try
            {
                await WriteErrorResponseAsync(id, -32603, "Internal error while handling the request.").ConfigureAwait(false);
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

        try
        {
            try
            {
                try
                {
                    CloseOutput();
                }
                finally
                {
                    _cts.Cancel();
                }
            }
            finally
            {
                FailAllPending(new IOException("The JSON-RPC connection was closed."));

                try
                {
                    if (_pumpTask is not null)
                    {
                        Task completed = await Task.WhenAny(_pumpTask, Task.Delay(_disposeGracePeriod)).ConfigureAwait(false);
                        if (ReferenceEquals(completed, _pumpTask))
                        {
                            try
                            {
                                await _pumpTask.ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                            }
                        }
                        else
                        {
                            _ = _pumpTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                        }
                    }
                }
                finally
                {
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
                }
            }
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Begins a new operation by acquiring the operation gate, verifying the instance is not disposed (throwing an OperationCanceledException if it is), and incrementing the active operation count.
    /// </summary>
    /// <exception cref="OperationCanceledException">Thrown when an error occurs during execution.</exception>
    private void BeginOperation()
    {
        lock (_operationGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new OperationCanceledException(_shutdownToken);
            }

            _activeOperations++;
        }
    }

    /// <summary>
    /// Ends the current operation by decrementing the active operation count and, when no operations remain, disposes the write lock, inbound request throttle, and cancellation token source.
    /// </summary>
    private void EndOperation()
    {
        lock (_operationGate)
        {
            if (--_activeOperations == 0)
            {
                _writeLock.Dispose();
                _inboundRequestThrottle.Dispose();
                _cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Represents a wire identifier that encapsulates numeric or textual values and provides methods to retrieve the originating and terminating node identifiers.
    /// </summary>
    private readonly struct WireId
    {
        private readonly long _numeric;
        private readonly string? _text;

        /// <summary>
        /// Initializes a new instance of the WireId struct with the specified numeric identifier and optional textual representation.
        /// </summary>
        /// <param name="numeric">The numeric.</param>
        /// <param name="text">The text.</param>
        private WireId(long numeric, string? text)
        {
            _numeric = numeric;
            _text = text;
        }

        /// <summary>
        /// Parses the given JSON node into a WireId, converting numeric values to the numeric identifier, string values to the string identifier, and throws an AcpProtocolException if the node is neither a number nor a string.
        /// </summary>
        /// <param name="node">The node.</param>
        /// <returns>The wire id result.</returns>
        /// <exception cref="AcpProtocolException">Thrown when an error occurs during execution.</exception>
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

        /// <summary>
        /// Creates a JsonValue node containing the stored text when it is not null, otherwise the numeric value.
        /// </summary>
        /// <returns>The json value result.</returns>
        public JsonValue ToNode() => _text is not null ? JsonValue.Create(_text)! : JsonValue.Create(_numeric)!;
    }
}
