using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// One ordered piece of an assistant turn: a run of text, a run of thinking, or a tool call, in the
/// actual sequence the agent emitted them. <see cref="ChatMessageViewModel.Text"/> and
/// <see cref="ChatMessageViewModel.ToolCalls"/> group everything by kind instead, which loses that
/// interleaving (a chat rendered from them always shows "all text, then all tool calls" even when a
/// tool call actually happened in between two text chunks). <see cref="ChatMessageViewModel.Parts"/>
/// is the ordered view used for rendering; Text/ToolCalls stay as they are since other code and
/// tests already depend on their grouped-by-kind shape.
/// </summary>
/// <remarks>Deliberately not observable: nothing subscribes to a part. The transcript repaint is
/// driven by <see cref="ChatMessageViewModel"/>'s own <c>Text</c> and <c>ThinkingVersion</c>
/// notifications, which <c>TranscriptHostProtocol.AffectsTranscript</c> honours, and the page
/// itself is re-rendered from the whole <see cref="ChatMessageViewModel.Parts"/> payload.</remarks>
public abstract class ChatMessagePart
{
}

/// <summary>
/// Represents a chat message part that holds plain text content and provides a builder-backed mechanism for appending text incrementally.
/// </summary>
public sealed class ChatTextPart : ChatMessagePart
{
    private readonly StringBuilder _builder;
    private string? _text;

    /// <summary>
    /// Initializes a new instance of the ChatTextPart class with the specified text, creating a mutable StringBuilder seeded with that content.
    /// </summary>
    /// <param name="text">The text.</param>
    internal ChatTextPart(string text)
    {
        _builder = new StringBuilder(text);
        _text = text;
    }

    /// <summary>The run's accumulated text, materialized lazily so streaming stays amortized O(1):
    /// the agent chooses both the chunk size and the total length, and re-concatenating the whole
    /// run per chunk is quadratic work on the UI thread.</summary>
    public string Text => _text ??= _builder.ToString();

    /// <summary>
    /// Appends the specified text chunk to the underlying string builder and invalidates the cached text value so it is regenerated on the next access.
    /// </summary>
    /// <param name="chunk">The chunk.</param>
    internal void Append(string chunk)
    {
        _builder.Append(chunk);
        _text = null;
    }
}

/// <summary>A run of Claude's thinking, shown in the transcript like the VS Code extension does -
/// Claude often answers a message sent mid-turn there. Never part of the reply's Text.</summary>
public sealed class ChatThinkingPart : ChatMessagePart
{
    private readonly StringBuilder _builder = new StringBuilder();
    private string? _text;

    /// <summary>
    /// Initializes a new instance of the ChatThinkingPart class for use by derived types or internal factory methods.
    /// </summary>
    internal ChatThinkingPart()
    {
    }

    /// <summary>
    /// Gets the text.
    /// </summary>
    public string Text => _text ??= _builder.ToString();

    /// <summary>
    /// Appends the specified text chunk to the underlying buffer and invalidates the cached text representation.
    /// </summary>
    /// <param name="chunk">The chunk.</param>
    internal void Append(string chunk)
    {
        _builder.Append(chunk);
        _text = null;
    }
}

/// <summary>
/// Represents a chat message part that carries a tool invocation, exposing its associated ToolCallCardViewModel for rendering the call&apos;s status and details in the conversation view.
/// </summary>
public sealed class ChatToolCallPart : ChatMessagePart
{
    /// <summary>
    /// Initializes a new instance of the ChatToolCallPart class with the specified tool call card view model.
    /// </summary>
    /// <param name="card">The card.</param>
    public ChatToolCallPart(ToolCallCardViewModel card)
    {
        Card = card;
    }

    /// <summary>
    /// Gets the card.
    /// </summary>
    public ToolCallCardViewModel Card { get; }
}
