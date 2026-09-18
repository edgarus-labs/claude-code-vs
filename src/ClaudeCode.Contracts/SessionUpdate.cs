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

    /// <summary>A chunk of the user's own prompt, replayed by the agent when resuming a session
    /// (e.g. via <see cref="IAcpAgentConnection.LoadSessionAsync"/>); never sent for the client's own
    /// live prompts.</summary>
    public sealed class UserMessageChunk : SessionUpdate
    {
        public UserMessageChunk(string text) => Text = text;

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
