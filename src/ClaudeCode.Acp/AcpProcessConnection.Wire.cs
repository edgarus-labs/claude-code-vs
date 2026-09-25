using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

public sealed partial class AcpProcessConnection
{
    // ---- outbound: agent -> client requests (fs/read_text_file, fs/write_text_file, session/request_permission) ----

    private Task<JsonNode?> HandleInboundRequestAsync(string method, JsonNode? @params, CancellationToken cancellationToken)
    {
        return method switch
        {
            "fs/read_text_file" => HandleReadTextFileAsync(RequireObject(@params, method), cancellationToken),
            "fs/write_text_file" => HandleWriteTextFileAsync(RequireObject(@params, method), cancellationToken),
            "session/request_permission" => HandleRequestPermissionAsync(RequireObject(@params, method), cancellationToken),
            "elicitation/create" => HandleCreateElicitationAsync(RequireObject(@params, method), cancellationToken),
            _ => throw new AcpRemoteException(-32601, $"Method not found: {method}"),
        };
    }

    private async Task<JsonNode?> HandleReadTextFileAsync(JsonObject @params, CancellationToken cancellationToken)
    {
        var handler = FileReadRequested;
        if (handler is null)
        {
            throw new AcpRemoteException(-32603, "No client handler registered for fs/read_text_file.");
        }

        string path = GetRequiredString(@params, "path");
        var args = new FileReadRequestEventArgs(path, GetOptionalInt(@params, "line"), GetOptionalInt(@params, "limit"));
        handler(this, args);
        string content = await WaitWithCancellationAsync(args.Response.Task, cancellationToken).ConfigureAwait(false);

        return new JsonObject { ["content"] = content };
    }

    private async Task<JsonNode?> HandleWriteTextFileAsync(JsonObject @params, CancellationToken cancellationToken)
    {
        var handler = FileWriteRequested;
        if (handler is null)
        {
            throw new AcpRemoteException(-32603, "No client handler registered for fs/write_text_file.");
        }

        string path = GetRequiredString(@params, "path");
        string content = GetRequiredString(@params, "content");
        var args = new FileWriteRequestEventArgs(path, content);
        handler(this, args);
        bool succeeded = await WaitWithCancellationAsync(args.Response.Task, cancellationToken).ConfigureAwait(false);
        if (!succeeded)
        {
            throw new AcpRemoteException(-32000, $"Failed to write file: {path}");
        }

        return new JsonObject(); // WriteTextFileResponse carries no fields.
    }

    private async Task<JsonNode?> HandleRequestPermissionAsync(JsonObject @params, CancellationToken cancellationToken)
    {
        var handler = PermissionRequested;
        if (handler is null)
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
            string optionId = await WaitWithCancellationAsync(args.Response.Task, cancellationToken).ConfigureAwait(false);

            return optionId == CancelledPermissionOptionId
                ? new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }
                : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId } };
        }
        finally
        {
            UntrackPendingPermission(sessionId, args);
        }
    }

    private async Task<JsonNode?> HandleCreateElicitationAsync(JsonObject @params, CancellationToken cancellationToken)
    {
        // Only form mode was advertised in `initialize`'s clientCapabilities.elicitation. ACP is
        // explicit that a request using a mode the client has not advertised produces -32602;
        // answering `decline` would instead read as "the user refused", destroying the only signal
        // that stops a conforming agent from retrying a url-mode (secret-bearing) flow as a form.
        if (GetOptionalString(@params, "mode") != "form")
        {
            throw new AcpRemoteException(-32602, "Unsupported elicitation mode; this client advertises 'form' only.");
        }

        var handler = ElicitationRequested;
        if (handler is null)
        {
            throw new AcpRemoteException(-32603, "No client handler registered for elicitation/create.");
        }

        // ACP also allows a request-scoped form (`requestId` in place of `sessionId`) for prompts
        // raised before any session exists. This client has no surface for one, so refuse it the way
        // an unadvertised mode is refused: -32602 names the limitation and lets a conforming agent
        // fall back, where AcpProtocolException would claim the client malfunctioned (-32603).
        string? sessionId = GetOptionalString(@params, "sessionId");
        if (sessionId is null)
        {
            throw new AcpRemoteException(-32602, "Only session-scoped elicitation is supported; 'sessionId' is required.");
        }

        string message = GetRequiredString(@params, "message");
        var schema = RequireObject(@params["requestedSchema"], "elicitation/create.requestedSchema");
        IReadOnlyList<ElicitationField> fields = ParseElicitationFields(schema);

        var args = new ElicitationRequestEventArgs(sessionId, message, fields);
        TrackPendingElicitation(sessionId, args);
        try
        {
            handler(this, args);
            ElicitationAnswer answer = await WaitWithCancellationAsync(args.Response.Task, cancellationToken).ConfigureAwait(false);
            return BuildElicitationResponse(answer, fields, schema);
        }
        finally
        {
            UntrackPendingElicitation(sessionId, args);
        }
    }

    private static IReadOnlyList<ElicitationField> ParseElicitationFields(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties)
        {
            return Array.Empty<ElicitationField>();
        }

        var fields = new List<ElicitationField>(properties.Count);
        foreach (KeyValuePair<string, JsonNode?> entry in properties)
        {
            if (entry.Value is JsonObject property)
            {
                fields.Add(ParseElicitationField(entry.Key, property));
            }
        }

        return fields;
    }

    private static ElicitationField ParseElicitationField(string key, JsonObject property)
    {
        string? title = GetOptionalString(property, "title");
        string? description = GetOptionalString(property, "description");
        string? type = GetOptionalString(property, "type");

        // ACP's StringPropertySchema declares both `oneOf` (titled options) and `enum` (bare values)
        // as single-select, and MultiSelectItems declares both `items.anyOf` and `items.enum`. Letting
        // the untitled forms fall through to Text would render a closed choice as a free-text box and
        // send back a value the agent was promised could not occur.
        if (type == "string")
        {
            if (property["oneOf"] is JsonArray oneOf)
            {
                return new ElicitationField(key, title, description, ElicitationFieldKind.SingleSelect, ParseElicitationOptions(oneOf));
            }

            if (property["enum"] is JsonArray plainEnum)
            {
                return new ElicitationField(key, title, description, ElicitationFieldKind.SingleSelect, ParsePlainEnumOptions(plainEnum));
            }
        }

        if (type == "array" && property["items"] is JsonObject items)
        {
            if (items["anyOf"] is JsonArray anyOf)
            {
                return new ElicitationField(key, title, description, ElicitationFieldKind.MultiSelect, ParseElicitationOptions(anyOf));
            }

            if (items["enum"] is JsonArray itemEnum)
            {
                return new ElicitationField(key, title, description, ElicitationFieldKind.MultiSelect, ParsePlainEnumOptions(itemEnum));
            }
        }

        // Any other JSON Schema property type (number/integer/boolean, or a plain string with no
        // enum/oneOf) - AskUserQuestion, the only realistic source of these requests, never emits them,
        // so a plain text field is a reasonable fallback rather than a dedicated renderer per type.
        return new ElicitationField(key, title, description, ElicitationFieldKind.Text, Array.Empty<ElicitationOption>());
    }

    // A JSON Schema `enum` carries bare values with no titles, so each value is also its own label.
    private static IReadOnlyList<ElicitationOption> ParsePlainEnumOptions(JsonArray values)
    {
        if (values.Count == 0)
        {
            return Array.Empty<ElicitationOption>();
        }

        var result = new List<ElicitationOption>(values.Count);
        foreach (JsonNode? entry in values)
        {
            if (entry is JsonValue value && value.TryGetValue<string>(out string? text))
            {
                result.Add(new ElicitationOption(text, text, null));
            }
        }

        return result;
    }

    private static IReadOnlyList<ElicitationOption> ParseElicitationOptions(JsonArray options)
    {
        if (options.Count == 0)
        {
            return Array.Empty<ElicitationOption>();
        }

        var result = new List<ElicitationOption>(options.Count);
        foreach (JsonNode? entry in options)
        {
            if (entry is JsonObject option)
            {
                // `title` is an optional JSON Schema annotation and `const` carries the option's
                // value; either one alone is enough to render and answer the option, so only an
                // option with neither is unusable.
                string? value = GetOptionalString(option, "const");
                string? title = GetOptionalString(option, "title");
                if (value is null && title is null)
                {
                    throw new AcpProtocolException("Missing or invalid required 'const' field.");
                }

                result.Add(new ElicitationOption(value ?? title!, title ?? value!, GetOptionalString(option, "description")));
            }
        }

        return result;
    }

    private static JsonObject BuildElicitationResponse(ElicitationAnswer answer, IReadOnlyList<ElicitationField> fields, JsonObject schema)
    {
        string action = answer.Action switch
        {
            ElicitationAction.Accept => "accept",
            ElicitationAction.Decline => "decline",
            _ => "cancel",
        };

        if (answer.Action != ElicitationAction.Accept)
        {
            return new JsonObject { ["action"] = action };
        }

        var content = new JsonObject();
        foreach (ElicitationField field in fields)
        {
            if (!answer.Content.TryGetValue(field.Key, out IReadOnlyList<string>? values) || values.Count == 0)
            {
                continue; // left blank - omit, rather than sending an empty string/array answer.
            }

            if (field.Kind != ElicitationFieldKind.MultiSelect && !IsStringTyped(schema, field.Key))
            {
                // ACP's ElicitationPropertySchema also covers boolean/number/integer, which render as
                // free text above; answering one with a JSON string would violate the very schema the
                // agent published, so leave it unanswered rather than sending "true" for a boolean.
                continue;
            }

            content[field.Key] = field.Kind == ElicitationFieldKind.MultiSelect
                ? new JsonArray(values.Select(value => (JsonNode)value).ToArray())
                : values[0];
        }

        return new JsonObject { ["action"] = action, ["content"] = content };
    }

    private static bool IsStringTyped(JsonObject schema, string key)
    {
        string? type = schema["properties"] is JsonObject properties && properties[key] is JsonObject property
            ? GetOptionalString(property, "type")
            : null;
        return type is null || type == "string";
    }

    // Awaits `task`, but also completes (with an OperationCanceledException) as soon as
    // `cancellationToken` fires - e.g. because the underlying connection is being torn down - so a
    // client-side event handler that never resolves its TaskCompletionSourceSlot (an unanswered
    // permission dialog, a stuck file read/write) can never leave this wait blocked forever. The slot
    // itself is not forced to a result: whatever eventually resolves it (if anything) still can.
    private static async Task<T> WaitWithCancellationAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellationTcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancellationTcs.TrySetCanceled(cancellationToken)))
        {
            Task<T> completed = await Task.WhenAny(task, cancellationTcs.Task).ConfigureAwait(false);
            return await completed.ConfigureAwait(false);
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

        string? sessionId;
        SessionUpdate? update;
        try
        {
            sessionId = GetOptionalString(obj, "sessionId");
            if (sessionId is null || obj["update"] is not JsonObject updateNode)
            {
                return;
            }

            update = ParseSessionUpdate(updateNode);
        }
        catch (Exception)
        {
            // Parsing runs inline on the JSON-RPC read pump, and an escaping exception ends the read
            // loop and faults every in-flight request. A tool_call without `toolCallId`, a malformed
            // config_option_update/available_commands_update, or an object System.Text.Json refuses
            // to materialize (a repeated key throws ArgumentException on the first property read)
            // degrades to a dropped notification instead - the same treatment usage_update and
            // session/list rows get. Only the parse is covered: a throwing SessionUpdate subscriber
            // is a client bug and still surfaces through Disconnected.
            return;
        }

        if (update is null)
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

            case "user_message_chunk":
                return new SessionUpdate.UserMessageChunk(ExtractChunkText(update));

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

            case "config_option_update":
                return new SessionUpdate.ConfigOptionsChanged(ParseConfigOptions(update, required: true));

            case "available_commands_update":
                return new SessionUpdate.AvailableCommandsChanged(ParseAvailableCommands(update));

            case "usage_update":
                return ParseUsageUpdate(update);

            default:
                // current_mode_update, etc. have no SessionUpdate subclass in ClaudeCode.Contracts
                // yet; silently ignored rather than throwing.
                return null;
        }
    }

    // { "used": 8300, "size": 200000, "cost": { "amount": 0.12, "currency": "USD" } } - size/cost optional.
    // Every number here is agent-supplied and unbounded on the wire, and this runs inline on the
    // JSON-RPC read pump: an OverflowException out of (decimal), or a wrapped (long) cast, would tear
    // down the whole connection instead of degrading one notification.
    private static SessionUpdate.UsageUpdate? ParseUsageUpdate(JsonObject update)
    {
        if (ClampToTokenCount(update["used"]) is not long used)
        {
            return null;
        }

        long? size = ClampToTokenCount(update["size"]);
        decimal? amount = null;
        string? currency = null;
        if (update["cost"] is JsonObject cost)
        {
            amount = ParseCostAmount(cost["amount"]);
            currency = GetOptionalString(cost, "currency");
        }

        return new SessionUpdate.UsageUpdate(used, size, amount, currency);
    }

    // `used`/`size` are uint64 in ACP: anything above long.MaxValue arrives as a double whose plain
    // cast wraps to a negative count, so saturate instead, and floor the (schema-invalid) negatives
    // at zero. Absent, non-numeric or NaN yields null - unknown, not zero.
    private static long? ClampToTokenCount(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out long exact))
        {
            return exact < 0L ? 0L : exact;
        }

        if (!value.TryGetValue<double>(out double approximate) || double.IsNaN(approximate))
        {
            return null;
        }

        if (approximate <= 0d)
        {
            return 0L;
        }

        return approximate >= long.MaxValue ? long.MaxValue : (long)approximate;
    }

    // decimal's range is far narrower than double's - (decimal)1e29 throws OverflowException, and ACP
    // bounds cost.amount at nothing. An unrepresentable (or NaN/infinite) cost degrades to "unknown".
    private static decimal? ParseCostAmount(JsonNode? node) =>
        node is JsonValue value
        && value.TryGetValue<double>(out double amount)
        && amount > -_maxRepresentableCostAmount
        && amount < _maxRepresentableCostAmount
            ? (decimal)amount
            : null;

    private static readonly double _maxRepresentableCostAmount = (double)decimal.MaxValue;

    private static IReadOnlyList<SessionSummary> ParseSessionSummaries(JsonObject response)
    {
        JsonArray sessions;
        try
        {
            sessions = response["sessions"] as JsonArray
                ?? throw new AcpProtocolException("Missing or invalid 'sessions' array.");
        }
        catch (ArgumentException)
        {
            // A repeated key anywhere in the response body surfaces when the object is first
            // materialized; report it as the same malformed-response failure the missing-array case
            // raises, so the caller's broad error handling answers "could not load history".
            throw new AcpProtocolException("Missing or invalid 'sessions' array.");
        }

        if (sessions.Count == 0)
        {
            return Array.Empty<SessionSummary>();
        }

        var result = new List<SessionSummary>(sessions.Count);
        foreach (JsonNode? entry in sessions)
        {
            // A single malformed row degrades that row only - the rest of the session history must
            // still reach the user, otherwise one bad entry hides every session behind "Could not
            // load session history". ACP marks these list items x-deserialize-skip-invalid-items.
            if (entry is not JsonObject session)
            {
                continue;
            }

            try
            {
                string? sessionId = GetOptionalString(session, "sessionId");
                string? cwd = GetOptionalString(session, "cwd");
                if (sessionId is null || cwd is null)
                {
                    continue;
                }

                string? updatedAtRaw = GetOptionalString(session, "updatedAt");
                DateTimeOffset? updatedAt = updatedAtRaw is not null
                    && DateTimeOffset.TryParse(updatedAtRaw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset parsed)
                        ? parsed
                        : null;
                result.Add(new SessionSummary(sessionId, cwd, GetOptionalString(session, "title"), updatedAt));
            }
            catch (ArgumentException)
            {
                // A repeated key in this row throws on its first property read; skip the row exactly
                // like the other malformed-row cases above.
                continue;
            }
        }

        return result;
    }

    private static IReadOnlyList<AvailableCommand> ParseAvailableCommands(JsonObject update)
    {
        if (update["availableCommands"] is not JsonArray commands)
        {
            throw new AcpProtocolException("Missing or invalid 'availableCommands' array.");
        }

        if (commands.Count == 0)
        {
            return Array.Empty<AvailableCommand>();
        }

        var result = new List<AvailableCommand>(commands.Count);
        foreach (JsonNode? entry in commands)
        {
            var command = entry as JsonObject ?? throw new AcpProtocolException("Invalid available command.");
            var input = command["input"] as JsonObject;
            result.Add(new AvailableCommand(
                GetRequiredString(command, "name"),
                GetRequiredString(command, "description"),
                input is null ? null : GetOptionalString(input, "hint")));
        }

        return result;
    }

    private static string ExtractChunkText(JsonObject update) =>
        update["content"] is JsonObject content ? ExtractTextFromContentBlock(content) ?? "" : "";

    private static IReadOnlyList<PlanEntry> ParsePlanEntries(JsonArray? array)
    {
        if (array is null || array.Count == 0)
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
        Locations = ParseToolCallLocationPaths(obj["locations"] as JsonArray),
        // claude-agent-acp: _meta.claudeCode.toolName is the Claude Code tool behind the call.
        IsSubagent = obj["_meta"] is JsonObject meta && meta["claudeCode"] is JsonObject claudeCode
            && GetOptionalString(claudeCode, "toolName") is "Agent" or "Task",
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
        if (array is null || array.Count == 0)
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

    private static IReadOnlyList<string> ParseToolCallLocationPaths(JsonArray? array)
    {
        if (array is null || array.Count == 0)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is JsonObject obj && GetOptionalString(obj, "path") is { Length: > 0 } path)
            {
                list.Add(path);
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
        ContentBlock.EmbeddedTextResource resource => ToWireEmbeddedTextResource(resource),
        ContentBlock.ResourceLink link => new JsonObject { ["type"] = "resource_link", ["uri"] = link.Uri, ["name"] = link.Name ?? link.Uri },
        _ => throw new NotSupportedException($"Unsupported ContentBlock type: {block.GetType()}"),
    };

    private static JsonObject ToWireEmbeddedTextResource(ContentBlock.EmbeddedTextResource resource)
    {
        var contents = new JsonObject { ["uri"] = resource.Uri, ["text"] = resource.Text };
        if (resource.MimeType is not null)
        {
            contents["mimeType"] = resource.MimeType;
        }

        return new JsonObject { ["type"] = "resource", ["resource"] = contents };
    }

    private static JsonArray BuildMcpServersArray(IReadOnlyList<McpServerConfig>? servers)
    {
        var array = new JsonArray();
        if (servers is null)
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

    private static IReadOnlyList<SessionConfigOption> ParseConfigOptions(JsonObject response, bool required = false)
    {
        JsonArray configOptions;
        try
        {
            if (response["configOptions"] is null && !required)
            {
                return Array.Empty<SessionConfigOption>();
            }

            configOptions = response["configOptions"] as JsonArray
                ?? throw new AcpProtocolException("Missing or invalid 'configOptions' array.");
        }
        catch (ArgumentException)
        {
            // A repeated key in the response body surfaces on first materialization; report it as the
            // same malformed-response failure the missing-array case raises.
            throw new AcpProtocolException("Missing or invalid 'configOptions' array.");
        }

        var result = new List<SessionConfigOption>(configOptions.Count);
        foreach (JsonNode? entry in configOptions)
        {
            if (entry is not JsonObject option)
            {
                throw new AcpProtocolException("Invalid session config option.");
            }

            try
            {
                // This client advertises no boolean-config extension. Ignore future option types
                // rather than interpreting their values as select strings.
                if (GetOptionalString(option, "type") != "select")
                {
                    continue;
                }

                var id = GetRequiredString(option, "id");
                var name = GetRequiredString(option, "name");
                var currentValue = GetRequiredString(option, "currentValue");
                if (option["options"] is not JsonArray values)
                {
                    throw new AcpProtocolException("Missing or invalid session config option values.");
                }

                var choices = new List<SessionConfigValue>();
                foreach (JsonNode? value in values)
                {
                    if (value is JsonObject group && group["options"] is JsonArray groupedValues)
                    {
                        foreach (JsonNode? groupedValue in groupedValues)
                        {
                            choices.Add(ParseConfigValue(groupedValue));
                        }
                    }
                    else
                    {
                        choices.Add(ParseConfigValue(value));
                    }
                }

                result.Add(new SessionConfigOption(id, name, GetOptionalString(option, "category"), currentValue, choices));
            }
            catch (ArgumentException)
            {
                // A repeated key in this option throws on its first property read; skip the option so
                // one malformed entry cannot hide the rest of the picker.
                continue;
            }
        }

        return result;
    }

    private static SessionConfigValue ParseConfigValue(JsonNode? node)
    {
        var value = node as JsonObject ?? throw new AcpProtocolException("Invalid session config value.");
        return new SessionConfigValue(GetRequiredString(value, "value"), GetRequiredString(value, "name"), GetOptionalString(value, "description"));
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
