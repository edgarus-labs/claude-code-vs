using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ClaudeCode.Contracts;

namespace ClaudeCode.Acp
{
    /// <summary>Translation between the ACP JSON wire shapes (https://agentclientprotocol.com/protocol/v1/schema)
    /// and the protocol-agnostic types in ClaudeCode.Contracts.</summary>
    public sealed partial class AcpProcessConnection
    {
        // ---- outbound: agent -> client requests (fs/read_text_file, fs/write_text_file, session/request_permission) ----

        private Task<JsonNode?> HandleInboundRequestAsync(string method, JsonNode? @params, System.Threading.CancellationToken cancellationToken)
        {
            return method switch
            {
                "fs/read_text_file" => HandleReadTextFileAsync(RequireObject(@params, method)),
                "fs/write_text_file" => HandleWriteTextFileAsync(RequireObject(@params, method)),
                "session/request_permission" => HandleRequestPermissionAsync(RequireObject(@params, method)),
                _ => throw new AcpRemoteException(-32601, $"Method not found: {method}"),
            };
        }

        private async Task<JsonNode?> HandleReadTextFileAsync(JsonObject @params)
        {
            var handler = FileReadRequested;
            if (handler == null)
            {
                throw new AcpRemoteException(-32603, "No client handler registered for fs/read_text_file.");
            }

            string path = GetRequiredString(@params, "path");
            var args = new FileReadRequestEventArgs(path, GetOptionalInt(@params, "line"), GetOptionalInt(@params, "limit"));
            handler(this, args);
            string content = await args.Response.Task.ConfigureAwait(false);
            return new JsonObject { ["content"] = content };
        }

        private async Task<JsonNode?> HandleWriteTextFileAsync(JsonObject @params)
        {
            var handler = FileWriteRequested;
            if (handler == null)
            {
                throw new AcpRemoteException(-32603, "No client handler registered for fs/write_text_file.");
            }

            string path = GetRequiredString(@params, "path");
            string content = GetRequiredString(@params, "content");
            var args = new FileWriteRequestEventArgs(path, content);
            handler(this, args);
            bool succeeded = await args.Response.Task.ConfigureAwait(false);
            if (!succeeded)
            {
                throw new AcpRemoteException(-32000, $"Failed to write file: {path}");
            }

            return new JsonObject(); // WriteTextFileResponse carries no fields.
        }

        private async Task<JsonNode?> HandleRequestPermissionAsync(JsonObject @params)
        {
            var handler = PermissionRequested;
            if (handler == null)
            {
                throw new AcpRemoteException(-32603, "No client handler registered for session/request_permission.");
            }

            string sessionId = GetRequiredString(@params, "sessionId");
            var toolCall = ParseToolCallUpdate(RequireObject(@params["toolCall"], "session/request_permission.toolCall"));
            var options = (@params["options"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(ParsePermissionOption)
                .ToList();

            var args = new PermissionRequestEventArgs(sessionId, toolCall, options);
            TrackPendingPermission(sessionId, args);
            try
            {
                handler(this, args);
                string optionId = await args.Response.Task.ConfigureAwait(false);
                return optionId == CancelledPermissionOptionId
                    ? new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }
                    : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId } };
            }
            finally
            {
                UntrackPendingPermission(sessionId, args);
            }
        }

        private static PermissionOption ParsePermissionOption(JsonObject obj) => new PermissionOption
        {
            OptionId = GetRequiredString(obj, "optionId"),
            Label = GetOptionalString(obj, "name") ?? "",
            Outcome = ParsePermissionOptionKind(GetOptionalString(obj, "kind")),
        };

        private static PermissionOutcome ParsePermissionOptionKind(string? kind) => kind switch
        {
            "allow_once" => PermissionOutcome.AllowOnce,
            "allow_always" => PermissionOutcome.AllowAlways,
            "reject_once" => PermissionOutcome.RejectOnce,
            "reject_always" => PermissionOutcome.RejectAlways,
            _ => PermissionOutcome.Cancelled,
        };

        // ---- inbound: agent -> client notifications (session/update) ----

        private void OnNotificationReceived(JsonRpcNotification notification)
        {
            if (notification.Method != "session/update")
            {
                return; // only session/update carries a SessionUpdate payload in v1; anything else is ignored.
            }

            if (notification.Params is not JsonObject obj)
            {
                return;
            }

            string? sessionId = GetOptionalString(obj, "sessionId");
            if (sessionId == null || obj["update"] is not JsonObject updateNode)
            {
                return;
            }

            SessionUpdate? update = ParseSessionUpdate(updateNode);
            if (update == null)
            {
                return; // sessionUpdate discriminator has no ClaudeCode.Contracts.SessionUpdate subclass (yet).
            }

            SessionUpdate?.Invoke(this, new SessionUpdateEventArgs(sessionId, update));
        }

        private static SessionUpdate? ParseSessionUpdate(JsonObject update)
        {
            string? kind = GetOptionalString(update, "sessionUpdate");
            switch (kind)
            {
                case "agent_message_chunk":
                    return new SessionUpdate.AgentMessageChunk(ExtractChunkText(update));
                case "agent_thought_chunk":
                    return new SessionUpdate.AgentThoughtChunk(ExtractChunkText(update));
                case "tool_call":
                case "tool_call_update":
                    // Contracts.ToolCallUpdate has no "unset means unchanged" representation, so a
                    // tool_call_update that omits e.g. `title` maps to the type's default ("") rather than
                    // preserving whatever the client previously recorded for that toolCallId. Callers that
                    // maintain a running dictionary keyed by ToolCallId should merge non-default fields in,
                    // not overwrite wholesale, until/unless the contract grows partial-update semantics.
                    return new SessionUpdate.ToolCall(ParseToolCallUpdate(update));
                case "plan":
                    return new SessionUpdate.Plan(ParsePlanEntries(update["entries"] as JsonArray));
                default:
                    // user_message_chunk (echo of our own prompt), available_commands_update,
                    // current_mode_update, usage_update, etc. have no SessionUpdate subclass in
                    // ClaudeCode.Contracts yet; silently ignored rather than throwing.
                    return null;
            }
        }

        private static string ExtractChunkText(JsonObject update) =>
            update["content"] is JsonObject content ? ExtractTextFromContentBlock(content) ?? "" : "";

        private static IReadOnlyList<PlanEntry> ParsePlanEntries(JsonArray? array)
        {
            if (array == null || array.Count == 0)
            {
                return Array.Empty<PlanEntry>();
            }

            var list = new List<PlanEntry>(array.Count);
            foreach (JsonNode? item in array)
            {
                if (item is JsonObject obj)
                {
                    list.Add(new PlanEntry
                    {
                        Content = GetOptionalString(obj, "content") ?? "",
                        Status = ParsePlanEntryStatus(GetOptionalString(obj, "status")),
                        // NOTE: ACP's PlanEntry also carries a `priority` (high/medium/low) with no equivalent
                        // property on ClaudeCode.Contracts.PlanEntry; intentionally dropped here.
                    });
                }
            }

            return list;
        }

        private static PlanEntryStatus ParsePlanEntryStatus(string? status) => status switch
        {
            "pending" => PlanEntryStatus.Pending,
            "in_progress" => PlanEntryStatus.InProgress,
            "completed" => PlanEntryStatus.Completed,
            _ => PlanEntryStatus.Pending,
        };

        private static ToolCallUpdate ParseToolCallUpdate(JsonObject obj) => new ToolCallUpdate
        {
            ToolCallId = GetRequiredString(obj, "toolCallId"),
            Title = GetOptionalString(obj, "title") ?? "",
            Kind = GetOptionalString(obj, "kind"),
            Status = ParseToolCallStatus(GetOptionalString(obj, "status")),
            Content = ParseToolCallContentArray(obj["content"] as JsonArray),
        };

        private static ToolCallStatus ParseToolCallStatus(string? status) => status switch
        {
            "pending" => ToolCallStatus.Pending,
            "in_progress" => ToolCallStatus.InProgress,
            "completed" => ToolCallStatus.Completed,
            "failed" => ToolCallStatus.Failed,
            _ => ToolCallStatus.Pending,
        };

        private static IReadOnlyList<ToolCallContent> ParseToolCallContentArray(JsonArray? array)
        {
            if (array == null || array.Count == 0)
            {
                return Array.Empty<ToolCallContent>();
            }

            var list = new List<ToolCallContent>(array.Count);
            foreach (JsonNode? item in array)
            {
                if (item is JsonObject obj)
                {
                    list.Add(ParseToolCallContent(obj));
                }
            }

            return list;
        }

        private static ToolCallContent ParseToolCallContent(JsonObject obj)
        {
            string type = GetOptionalString(obj, "type") ?? "content";
            switch (type)
            {
                case "diff":
                    return new ToolCallContent
                    {
                        Path = GetOptionalString(obj, "path"),
                        OldText = GetOptionalString(obj, "oldText"),
                        NewText = GetOptionalString(obj, "newText"),
                    };
                case "terminal":
                    // ACP's embedded-terminal content (a live terminal reference by id) has no equivalent
                    // field on Contracts.ToolCallContent; surface a readable placeholder instead of dropping it.
                    string terminalId = GetOptionalString(obj, "terminalId") ?? "?";
                    return new ToolCallContent { Text = $"[terminal output: {terminalId}]" };
                case "content":
                default:
                    return obj["content"] is JsonObject inner
                        ? new ToolCallContent { Text = ExtractTextFromContentBlock(inner) }
                        : new ToolCallContent();
            }
        }

        private static string? ExtractTextFromContentBlock(JsonObject block) => GetOptionalString(block, "type") switch
        {
            "text" => GetOptionalString(block, "text"),
            "resource_link" => GetOptionalString(block, "uri"),
            // Images/audio/embedded resources have no plain-text Contracts representation; dropped.
            _ => null,
        };

        // ---- outbound: client -> agent prompt content ----

        private static JsonObject ToWireContentBlock(ContentBlock block) => block switch
        {
            ContentBlock.Text t => new JsonObject { ["type"] = "text", ["text"] = t.Value },
            ContentBlock.Image img => new JsonObject { ["type"] = "image", ["mimeType"] = img.MimeType, ["data"] = img.Base64Data },
            ContentBlock.ResourceLink link => new JsonObject { ["type"] = "resource_link", ["uri"] = link.Uri, ["name"] = link.Name ?? link.Uri },
            _ => throw new NotSupportedException($"Unsupported ContentBlock type: {block.GetType()}"),
        };

        private static JsonArray BuildMcpServersArray(IReadOnlyList<McpServerConfig>? servers)
        {
            var array = new JsonArray();
            if (servers == null)
            {
                return array;
            }

            foreach (McpServerConfig server in servers)
            {
                var env = new JsonArray();
                foreach (var variable in server.Env)
                {
                    env.Add(new JsonObject { ["name"] = variable.Key, ["value"] = variable.Value });
                }

                var args = new JsonArray();
                foreach (string arg in server.Args)
                {
                    args.Add(arg);
                }

                array.Add(new JsonObject
                {
                    ["name"] = server.Name,
                    ["command"] = server.Command,
                    ["args"] = args,
                    ["env"] = env,
                });
            }

            return array;
        }

        // ---- small JSON accessor helpers ----

        private static JsonObject RequireObject(JsonNode? node, string context) =>
            node as JsonObject ?? throw new AcpRemoteException(-32602, $"Invalid params for {context}: expected a JSON object.");

        private static string GetRequiredString(JsonObject obj, string property) =>
            GetOptionalString(obj, property) ?? throw new AcpProtocolException($"Missing or invalid required '{property}' field.");

        private static string? GetOptionalString(JsonObject obj, string property) =>
            obj.TryGetPropertyValue(property, out var node) && node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

        private static int? GetOptionalInt(JsonObject obj, string property)
        {
            if (!obj.TryGetPropertyValue(property, out var node) || node is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue<int>(out var i))
            {
                return i;
            }

            if (value.TryGetValue<long>(out var l))
            {
                return (int)l;
            }

            return value.TryGetValue<double>(out var d) ? (int)d : null;
        }
    }
}
