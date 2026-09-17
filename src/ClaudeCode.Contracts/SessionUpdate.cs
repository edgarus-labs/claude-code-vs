using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public abstract class SessionUpdate
{
    public sealed class AgentMessageChunk : SessionUpdate
    {
        public AgentMessageChunk(string text) => Text = text;

        public string Text { get; }
    }

    public sealed class AgentThoughtChunk : SessionUpdate
    {
        public AgentThoughtChunk(string text) => Text = text;

        public string Text { get; }
    }

    public sealed class ToolCall : SessionUpdate
    {
        public ToolCall(ToolCallUpdate call) => Call = call;

        public ToolCallUpdate Call { get; }
    }

    public sealed class Plan : SessionUpdate
    {
        public Plan(IReadOnlyList<PlanEntry> entries) => Entries = entries;

        public IReadOnlyList<PlanEntry> Entries { get; }
    }

    public sealed class ConfigOptionsChanged : SessionUpdate
    {
        public ConfigOptionsChanged(IReadOnlyList<SessionConfigOption> configOptions) => ConfigOptions = configOptions;

        public IReadOnlyList<SessionConfigOption> ConfigOptions { get; }
    }

    public sealed class AvailableCommandsChanged : SessionUpdate
    {
        public AvailableCommandsChanged(IReadOnlyList<AvailableCommand> commands) => Commands = commands;

        public IReadOnlyList<AvailableCommand> Commands { get; }
    }

    public sealed class TurnEnded : SessionUpdate
    {
        public TurnEnded(string stopReason) => StopReason = stopReason;

        public string StopReason { get; }
    }
}
