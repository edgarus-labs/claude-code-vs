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

internal delegate Task<JsonNode?> JsonRpcRequestHandler(string method, JsonNode? @params, CancellationToken cancellationToken);
