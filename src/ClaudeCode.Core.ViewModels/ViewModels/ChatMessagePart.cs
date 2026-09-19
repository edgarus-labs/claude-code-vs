using CommunityToolkit.Mvvm.ComponentModel;

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
    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
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
