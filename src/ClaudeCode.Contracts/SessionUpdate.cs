using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public abstract class SessionUpdate
{
    public sealed class AgentMessageChunk : SessionUpdate
    {
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
        public UserMessageChunk(string text) => Text = text;

        /// <summary>
        /// Gets the text.
        /// </summary>
        public string Text { get; }
    }

    public sealed class AgentThoughtChunk : SessionUpdate
    {
        public AgentThoughtChunk(string text) => Text = text;

        /// <summary>
        /// Gets the text.
        /// </summary>
        public string Text { get; }
    }

    public sealed class ToolCall : SessionUpdate
    {
        public ToolCall(ToolCallUpdate call) => Call = call;

        /// <summary>
        /// Gets the call.
        /// </summary>
        public ToolCallUpdate Call { get; }
    }

    public sealed class Plan : SessionUpdate
    {
        public Plan(IReadOnlyList<PlanEntry> entries) => Entries = entries;

        /// <summary>
        /// Gets the collection of entries.
        /// </summary>
        public IReadOnlyList<PlanEntry> Entries { get; }
    }

    public sealed class ConfigOptionsChanged : SessionUpdate
    {
        public ConfigOptionsChanged(IReadOnlyList<SessionConfigOption> configOptions) => ConfigOptions = configOptions;

        /// <summary>
        /// Gets the collection of config options.
        /// </summary>
        public IReadOnlyList<SessionConfigOption> ConfigOptions { get; }
    }

    public sealed class AvailableCommandsChanged : SessionUpdate
    {
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
