using ClaudeCode.Contracts;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.VsControl.Mcp;

public sealed class McpServer
{
    private const string _serverName = "claude-code-vscontrol-mcp";
    private const string _serverVersion = "1.0.0";
    private const string _defaultProtocolVersion = "2024-11-05";

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
        string protocolVersion = _defaultProtocolVersion;
        if (@params is JsonObject paramsObject
            && paramsObject.TryGetPropertyValue("protocolVersion", out var versionNode)
            && versionNode is JsonValue versionValue
            && versionValue.TryGetValue(out string? requestedVersion)
            && !string.IsNullOrEmpty(requestedVersion))
        {
            // Echo the client's requested version back: this is a fixed-capability server, not a
            // version-negotiating one, so there is nothing to actually negotiate.
            protocolVersion = requestedVersion;
        }

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

    private static JsonObject CreateToolResult(bool isError, string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private async Task WriteResponseAsync(JsonObject response, CancellationToken cancellationToken)
    {
        string json = response.ToJsonString();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(json).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
