using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// One ordered piece of an assistant turn: a run of text, a run of thinking, or a tool call, in the
/// sequence the agent emitted them.
/// </summary>
public abstract class ChatMessagePart
{
}

public sealed class ChatTextPart : ChatMessagePart
{
    private readonly StringBuilder _builder;
    private string? _text;

    internal ChatTextPart(string text)
    {
        _builder = new StringBuilder(text);
        _text = text;
    }

    /// <summary>The run's accumulated text.</summary>
    public string Text => _text ??= _builder.ToString();

    internal void Append(string chunk)
    {
        _builder.Append(chunk);
        _text = null;
    }
}

/// <summary>A run of Claude's thinking, shown in the transcript. Never part of the reply's Text.</summary>
public sealed class ChatThinkingPart : ChatMessagePart
{
    private readonly StringBuilder _builder = new StringBuilder();
    private string? _text;

    internal ChatThinkingPart()
    {
    }

    public string Text => _text ??= _builder.ToString();

    internal void Append(string chunk)
    {
        _builder.Append(chunk);
        _text = null;
    }
}

public sealed class ChatToolCallPart : ChatMessagePart
{
    public ChatToolCallPart(ToolCallCardViewModel card)
    {
        Card = card;
    }

    public ToolCallCardViewModel Card { get; }
}
