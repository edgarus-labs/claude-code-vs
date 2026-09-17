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
