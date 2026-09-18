using System.Text.Json.Nodes;

namespace ClaudeCode.Acp;

internal sealed class JsonRpcNotification
{
    public JsonRpcNotification(string method, JsonNode? @params)
    {
        Method = method;
        Params = @params;
    }

    public string Method { get; }

    public JsonNode? Params { get; }
}
