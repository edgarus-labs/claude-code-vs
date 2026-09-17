using System.Text.Json.Nodes;

namespace ClaudeCode.VsControl.Mcp;

/// <summary>Builds outgoing JSON-RPC 2.0 response envelopes as detached <see cref="JsonObject"/> trees.</summary>
public static class JsonRpcMessages
{
    /// <summary>
    /// Builds a JSON-RPC success response. <paramref name="id"/> is cloned so the caller may reuse the
    /// original node (still parented under the parsed request) without an "already has a parent" failure.
    /// </summary>
    public static JsonObject CreateSuccessResponse(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    };

    /// <summary>Builds a JSON-RPC error response with the given JSON-RPC error <paramref name="code"/>.</summary>
    public static JsonObject CreateErrorResponse(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };
}
