using System.Text.Json.Nodes;

namespace ClaudeCode.Acp;

/// <summary>
/// Represents a JSON-RPC notification that carries the method name and optional parameters.
/// </summary>
internal sealed class JsonRpcNotification
{
    /// <summary>
    /// Initializes a new instance of the JsonRpcNotification class with the specified method name and optional parameters.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="params">The params.</param>
    public JsonRpcNotification(string method, JsonNode? @params)
    {
        Method = method;
        Params = @params;
    }

    /// <summary>
    /// Gets the method.
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// Gets the params.
    /// </summary>
    public JsonNode? Params { get; }
}
