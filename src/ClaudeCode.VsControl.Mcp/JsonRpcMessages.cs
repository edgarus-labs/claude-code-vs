using System.Text.Json.Nodes;

namespace ClaudeCode.VsControl.Mcp;

/// <summary>
/// Provides static factory methods for constructing JSON-RPC 2.0 success and error response messages.
/// </summary>
public static class JsonRpcMessages
{
    /// <summary>
    /// Creates a JSON-RPC 2.0 success response object containing a deep clone of the specified request identifier and the provided result payload.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="result">The result.</param>
    /// <returns>The json object result.</returns>
    public static JsonObject CreateSuccessResponse(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    };

    /// <summary>
    /// Creates a JSON-RPC 2.0 error response object containing the specified error code and message, with a deep-cloned copy of the request identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="code">The code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The json object result.</returns>
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
