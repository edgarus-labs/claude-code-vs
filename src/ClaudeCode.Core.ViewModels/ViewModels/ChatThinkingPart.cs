using System.Text;

namespace ClaudeCode.Core.ViewModels;

/// <summary>A run of Claude's thinking, shown in the transcript. Never part of the reply's Text.</summary>
public sealed class ChatThinkingPart : ChatMessagePart
{
    private readonly StringBuilder _builder = new StringBuilder();
    private string? _text;

    internal ChatThinkingPart()
    {
    }

    /// <summary>
    /// Gets the text.
    /// </summary>
    public string Text => _text ??= _builder.ToString();

    internal void Append(string chunk)
    {
        _builder.Append(chunk);
        _text = null;
    }
}
