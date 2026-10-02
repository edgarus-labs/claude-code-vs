using System.Text;

namespace ClaudeCode.Core.ViewModels;

public sealed class ChatToolCallPart : ChatMessagePart
{
    public ChatToolCallPart(ToolCallCardViewModel card)
    {
        Card = card;
    }

    /// <summary>
    /// Gets the card.
    /// </summary>
    public ToolCallCardViewModel Card { get; }
}
