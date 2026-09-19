using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// One ordered piece of an assistant turn: either a run of text or a tool call, in the actual
/// sequence the agent emitted them. <see cref="ChatMessageViewModel.Text"/> and
/// <see cref="ChatMessageViewModel.ToolCalls"/> group everything by kind instead, which loses that
/// interleaving (a chat rendered from them always shows "all text, then all tool calls" even when a
/// tool call actually happened in between two text chunks). <see cref="ChatMessageViewModel.Parts"/>
/// is the ordered view used for rendering; Text/ToolCalls stay as they are since other code and
/// tests already depend on their grouped-by-kind shape.
/// </summary>
public abstract class ChatMessagePart : ObservableObject
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

    /// <summary>The run's accumulated text, materialized lazily so streaming stays amortized O(1):
    /// the agent chooses both the chunk size and the total length, and re-concatenating the whole
    /// run per chunk is quadratic work on the UI thread.</summary>
    public string Text => _text ??= _builder.ToString();

    internal void Append(string chunk)
    {
        _builder.Append(chunk);
        _text = null;
        OnPropertyChanged(nameof(Text));
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
