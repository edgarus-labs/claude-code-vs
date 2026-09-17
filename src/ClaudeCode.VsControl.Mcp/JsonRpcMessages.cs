using System.Text.Json.Nodes;

namespace ClaudeCode.VsControl.Mcp;

public static class JsonRpcMessages
{
    public static JsonObject CreateSuccessResponse(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    };

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
