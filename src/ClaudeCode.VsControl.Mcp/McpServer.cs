using ClaudeCode.Contracts;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.VsControl.Mcp;

public sealed class McpServer : IDisposable
{
    private const string _serverName = "claude-code-vscontrol-mcp";
    private const string _serverVersion = "1.0.0";
    private static readonly string[] _supportedProtocolVersions = { "2024-11-05" };

    // Hard cap on tool-result text returned to the model: 256 KiB of UTF-16 chars. Guards against an
    // oversized VS response (e.g. a huge file body) blowing up the agent's context.
    private const int _maxToolResultTextLength = 262_144;
    private const string _truncationSuffix = "\n\n[truncated: response exceeded 256KB]";

    // VsControl tool output originates from the open workspace/solution (file contents, build output,
    // diagnostics) and from the ACP agent's own tool arguments - both untrusted with respect to the
    // model. Delimiting it marks it as data to reason about, never as instructions to follow.
    private const string _untrustedOutputPrefix = "<<<UNTRUSTED_TOOL_OUTPUT>>>\n";
    private const string _untrustedOutputSuffix = "\n<<<END_UNTRUSTED_TOOL_OUTPUT>>>";

    private readonly VsControlPipeClient _pipeClient;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public McpServer(VsControlPipeClient pipeClient, TextReader input, TextWriter output)
    {
        _pipeClient = pipeClient;
        _input = input;
        _output = output;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break; // stdin closed (EOF): clean shutdown.
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Sequential by design (F5): each line is fully handled - including its own
            // VsControlPipeClient.SendAsync call, which now enforces its own per-request timeout -
            // before the next stdin line is read. Wrapping this call in an additional outer timeout
            // would let a slow request's line be abandoned mid-flight while its response was still
            // pending, letting a later request's response reach stdout first: that would break
            // response ordering, which JSON-RPC callers rely on. The per-request timeout inside
            // SendAsync is therefore the sole bound on how long any one call can block this loop.
            await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? throw new FormatException("Expected a JSON object.");
        }
        catch (Exception ex)
        {
            // No response is sent for an unparsable line: JSON-RPC requires echoing the caller's
            // `id`, which cannot be recovered from JSON that failed to parse. Whichever caller sent
            // this line will wait for a reply that never arrives - a limitation of line-oriented
            // JSON-RPC without a full batch/error-recovery story, not a bug in this handler.
            await Console.Error.WriteLineAsync(
                $"ClaudeCode.VsControl.Mcp: ignoring malformed request line ({ex.Message}).").ConfigureAwait(false);

            return;
        }

        JsonNode? id = request.TryGetPropertyValue("id", out var idNode) ? idNode : null;

        string? method = null;
        if (request.TryGetPropertyValue("method", out var methodNode) && methodNode is JsonValue methodValue)
        {
            methodValue.TryGetValue(out method);
        }

        JsonNode? @params = request.TryGetPropertyValue("params", out var paramsNode) ? paramsNode : null;

        if (id is null)
        {
            // JSON-RPC notification: no response is ever sent, regardless of method.
            return;
        }

        if (method is null)
        {
            await WriteResponseAsync(JsonRpcMessages.CreateErrorResponse(id, -32600, "Invalid Request: missing 'method'."), cancellationToken).ConfigureAwait(false);

            return;
        }

        JsonObject response = method switch
        {
            "initialize" => HandleInitialize(id, @params),
            "ping" => JsonRpcMessages.CreateSuccessResponse(id, new JsonObject()),
            "tools/list" => HandleToolsList(id),
            "tools/call" => await HandleToolsCallAsync(id, @params, cancellationToken).ConfigureAwait(false),
            _ => JsonRpcMessages.CreateErrorResponse(id, -32601, $"Method not found: {method}"),
        };

        await WriteResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject HandleInitialize(JsonNode id, JsonNode? @params)
    {
        string protocolVersion = _supportedProtocolVersions[0];
        if (@params is JsonObject paramsObject
            && paramsObject.TryGetPropertyValue("protocolVersion", out var versionNode)
            && versionNode is JsonValue versionValue
            && versionValue.TryGetValue(out string? requestedVersion)
            && !string.IsNullOrEmpty(requestedVersion)
            && Array.IndexOf(_supportedProtocolVersions, requestedVersion) >= 0)
        {
            protocolVersion = requestedVersion;
        }
        // Else: unrecognized/missing version - respond with the newest version this fixed-capability
        // server actually supports instead of blindly echoing whatever the client asked for.

        var result = new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = _serverName, ["version"] = _serverVersion },
        };

        return JsonRpcMessages.CreateSuccessResponse(id, result);
    }

    private static JsonObject HandleToolsList(JsonNode id)
    {
        var toolsArray = new JsonArray();
        foreach (var tool in VsControlToolCatalog.Tools)
        {
            toolsArray.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchemaJson),
            });
        }

        return JsonRpcMessages.CreateSuccessResponse(id, new JsonObject { ["tools"] = toolsArray });
    }

    private async Task<JsonObject> HandleToolsCallAsync(JsonNode id, JsonNode? @params, CancellationToken cancellationToken)
    {
        if (@params is not JsonObject paramsObject)
        {
            return JsonRpcMessages.CreateErrorResponse(id, -32602, "Invalid params: 'tools/call' requires an object with a 'name' field.");
        }

        string? toolName = null;
        if (paramsObject.TryGetPropertyValue("name", out var nameNode) && nameNode is JsonValue nameValue)
        {
            nameValue.TryGetValue(out toolName);
        }

        if (string.IsNullOrEmpty(toolName))
        {
            return JsonRpcMessages.CreateErrorResponse(id, -32602, "Invalid params: 'tools/call' requires a string 'name'.");
        }

        if (!VsControlToolCatalog.Tools.Any(t => t.Name == toolName))
        {
            return JsonRpcMessages.CreateErrorResponse(id, -32602, $"Unknown tool: {toolName}");
        }

        JsonNode? argumentsNode = paramsObject.TryGetPropertyValue("arguments", out var argsNode) ? argsNode : null;
        string paramsJson = argumentsNode is null ? "{}" : argumentsNode.ToJsonString();

        VsControlResponse vsResponse;
        try
        {
            var vsRequest = new VsControlRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Method = toolName,
                ParamsJson = paramsJson,
            };
            vsResponse = await _pipeClient.SendAsync(vsRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Pipe not connected / VS host not up / connection dropped mid-flight: a clean MCP tool
            // error, never an unhandled exception that would crash this process.
            return JsonRpcMessages.CreateSuccessResponse(id, CreateToolResult(isError: true, $"Failed to reach the Visual Studio control pipe: {ex.Message}"));
        }

        if (!string.IsNullOrEmpty(vsResponse.Error))
        {
            return JsonRpcMessages.CreateSuccessResponse(id, CreateToolResult(isError: true, vsResponse.Error!));
        }

        return JsonRpcMessages.CreateSuccessResponse(id, CreateToolResult(isError: false, vsResponse.ResultJson ?? "{}"));
    }

    // A VS result may carry one binary attachment under this key: { mimeType, data (base64) }. It is
    // lifted out into an MCP image content block so the model sees the picture rather than a wall of
    // base64 in its text (and so the text cap above never truncates the image).
    private const string _imagePropertyName = "_image";

    private static JsonObject CreateToolResult(bool isError, string text)
    {
        JsonObject? image = null;
        if (!isError && text.Length > 0 && text[0] == '{')
        {
            image = ExtractImage(ref text);
        }

        var result = CreateTextToolResult(isError, text);
        if (image is not null)
        {
            ((JsonArray)result["content"]!).Add(image);
        }

        return result;
    }

    private static JsonObject? ExtractImage(ref string text)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (root is null || !root.TryGetPropertyValue(_imagePropertyName, out var imageNode) || imageNode is not JsonObject imageObject)
        {
            return null;
        }

        // A malformed _image (non-string children) must degrade to the plain text result, never throw:
        // CreateToolResult runs outside the tools/call try/catch, so an exception here kills the sidecar.
        string? mimeType = TryGetString(imageObject, "mimeType");
        string? data = TryGetString(imageObject, "data");
        root.Remove(_imagePropertyName);
        text = root.ToJsonString();
        if (string.IsNullOrEmpty(mimeType) || string.IsNullOrEmpty(data))
        {
            return null;
        }

        return new JsonObject { ["type"] = "image", ["mimeType"] = mimeType, ["data"] = data };
    }

    private static string? TryGetString(JsonObject owner, string propertyName)
        => owner[propertyName] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static JsonObject CreateTextToolResult(bool isError, string text)
    {
        // Escape marker characters before adding the outer boundary; workspace content must not
        // manufacture an in-band closing marker. This labels data, not a model-enforced sandbox.
        text = text.Replace("<<<", "\\u003C\\u003C\\u003C", StringComparison.Ordinal);
        if (text.Length > _maxToolResultTextLength)
        {
            text = string.Concat(text.AsSpan(0, _maxToolResultTextLength), _truncationSuffix);
        }

        text = _untrustedOutputPrefix + text + _untrustedOutputSuffix;

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };
    }

    private async Task WriteResponseAsync(JsonObject response, CancellationToken cancellationToken)
    {
        string json = response.ToJsonString();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(json).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
