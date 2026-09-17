using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

internal delegate Task<JsonNode?> JsonRpcRequestHandler(string method, JsonNode? @params, CancellationToken cancellationToken);
