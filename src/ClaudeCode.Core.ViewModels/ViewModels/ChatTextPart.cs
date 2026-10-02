using System.Text;

namespace ClaudeCode.Core.ViewModels;

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
