using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public abstract class SessionUpdate
{
    /// <summary>
    /// Represents a session update containing a chunk of agent-generated message text streamed to the client.
    /// </summary>
    public sealed class AgentMessageChunk : SessionUpdate
    {
        /// <summary>
        /// Initializes a new instance of the AgentMessageChunk class with the specified message text.
        /// </summary>
        /// <param name="text">The text.</param>
        public AgentMessageChunk(string text) => Text = text;

        /// <summary>
        /// Gets the text.
        /// </summary>
        public string Text { get; }
    }

    /// <summary>A chunk of the user's own prompt, replayed by the agent when resuming a session
    /// (e.g. via <see cref="IAcpAgentConnection.LoadSessionAsync"/>); never sent for the client's own
    /// live prompts.</summary>
    public sealed class UserMessageChunk : SessionUpdate
    {
        /// <summary>
        /// Initializes a new instance of the UserMessageChunk class with the specified message text.
        /// </summary>
        /// <param name="text">The text.</param>
        public UserMessageChunk(string text) => Text = text;

        /// <summary>
        /// Gets the text.
        /// </summary>
        public string Text { get; }
    }

    /// <summary>
    /// Represents an agent thought chunk in a session update stream, carrying the incremental text of the agent&apos;s internal reasoning.
    /// </summary>
    public sealed class AgentThoughtChunk : SessionUpdate
    {
        /// <summary>
        /// Initializes a new instance of the AgentThoughtChunk class with the specified thought text.
        /// </summary>
        /// <param name="text">The text.</param>
        public AgentThoughtChunk(string text) => Text = text;

        /// <summary>
        /// Gets the text.
        /// </summary>
        public string Text { get; }
    }

    /// <summary>
    /// Represents a session update notification that reports a tool call initiated by the agent, encapsulating its associated ToolCallUpdate details.
    /// </summary>
    public sealed class ToolCall : SessionUpdate
    {
        /// <summary>
        /// Initializes a new instance of the ToolCall class with the specified ToolCallUpdate.
        /// </summary>
        /// <param name="call">The call.</param>
        public ToolCall(ToolCallUpdate call) => Call = call;

        /// <summary>
        /// Gets the call.
        /// </summary>
        public ToolCallUpdate Call { get; }
    }

    /// <summary>
    /// Represents a plan containing a read-only collection of plan entries.
    /// </summary>
    public sealed class Plan : SessionUpdate
    {
        /// <summary>
        /// Initializes a new instance of the Plan class with the specified collection of plan entries.
        /// </summary>
        /// <param name="entries">The collection of entries.</param>
        public Plan(IReadOnlyList<PlanEntry> entries) => Entries = entries;

        /// <summary>
        /// Gets the collection of entries.
        /// </summary>
        public IReadOnlyList<PlanEntry> Entries { get; }
    }

    /// <summary>
    /// Represents a session update event that occurs when configuration options are changed, containing the updated configuration options.
    /// </summary>
    public sealed class ConfigOptionsChanged : SessionUpdate
    {
        /// <summary>
        /// Initializes a new instance of the ConfigOptionsChanged class with the specified configuration options.
        /// </summary>
        /// <param name="configOptions">The collection of config options.</param>
        public ConfigOptionsChanged(IReadOnlyList<SessionConfigOption> configOptions) => ConfigOptions = configOptions;

        /// <summary>
        /// Gets the collection of config options.
        /// </summary>
        public IReadOnlyList<SessionConfigOption> ConfigOptions { get; }
    }

    /// <summary>
    /// Represents a session update event that notifies when the available commands have changed, containing the updated list of available commands.
    /// </summary>
    public sealed class AvailableCommandsChanged : SessionUpdate
    {
        /// <summary>
        /// Initializes a new instance of the AvailableCommandsChanged class with the specified list of available commands.
        /// </summary>
        /// <param name="commands">The collection of commands.</param>
        public AvailableCommandsChanged(IReadOnlyList<AvailableCommand> commands) => Commands = commands;

        /// <summary>
        /// Gets the collection of commands.
        /// </summary>
        public IReadOnlyList<AvailableCommand> Commands { get; }
    }

    /// <summary>Context-window usage reported by the agent (ACP <c>usage_update</c>): tokens currently
    /// used, the window size, and optionally the session's running cost.</summary>
    public sealed class UsageUpdate : SessionUpdate
    {
        /// <summary>
        /// Initializes a new instance of the UsageUpdate class with the specified token usage, context window size, cost amount, and currency.
        /// </summary>
        /// <param name="usedTokens">The used tokens.</param>
        /// <param name="contextWindowSize">The context window size.</param>
        /// <param name="costAmount">The cost amount.</param>
        /// <param name="costCurrency">The cost currency.</param>
        public UsageUpdate(long usedTokens, long? contextWindowSize, decimal? costAmount, string? costCurrency)
        {
            UsedTokens = usedTokens;
            ContextWindowSize = contextWindowSize;
            CostAmount = costAmount;
            CostCurrency = costCurrency;
        }

        /// <summary>
        /// Gets the used tokens.
        /// </summary>
        public long UsedTokens { get; }

        /// <summary>
        /// Gets the context window size.
        /// </summary>
        public long? ContextWindowSize { get; }

        /// <summary>
        /// Gets the cost amount.
        /// </summary>
        public decimal? CostAmount { get; }

        /// <summary>
        /// Gets the cost currency.
        /// </summary>
        public string? CostCurrency { get; }
    }

    /// <summary>
    /// Represents a session update event indicating the end of a turn with the specified stop reason.
    /// </summary>
    public sealed class TurnEnded : SessionUpdate
    {
        /// <summary>
        /// Initializes a new instance of the TurnEnded class with the specified stop reason.
        /// </summary>
        /// <param name="stopReason">The stop reason.</param>
        public TurnEnded(string stopReason) => StopReason = stopReason;

        /// <summary>
        /// Gets the stop reason.
        /// </summary>
        public string StopReason { get; }
    }
}
