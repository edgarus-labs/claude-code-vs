using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

public sealed class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatRole role, string text = "")
    {
        Role = role;
        text ??= string.Empty;
        _text = MarkdownSafetyLimits.LimitMarkdownLength(text);
        _textBuilder = new StringBuilder(_text);
        _isTruncated = text.Length > MarkdownSafetyLimits.MaxMarkdownLength;
    }

    public ChatRole Role { get; }

    private readonly StringBuilder _textBuilder;
    private string? _text;
    private bool _isTruncated;

    public string Text => _text ??= _textBuilder.ToString();

    public ObservableCollection<ToolCallCardViewModel> ToolCalls { get; } = new ObservableCollection<ToolCallCardViewModel>();

    public void AppendText(string chunk)
    {
        if (_isTruncated || string.IsNullOrEmpty(chunk))
        {
            return;
        }

        var remaining = MarkdownSafetyLimits.MaxMarkdownLength - _textBuilder.Length;
        _textBuilder.Append(chunk, 0, Math.Min(chunk.Length, remaining));
        if (chunk.Length > remaining)
        {
            _textBuilder.Append(MarkdownSafetyLimits.TruncationNotice);
            _isTruncated = true;
        }

        _text = null;
        OnPropertyChanged(nameof(Text));
    }
}
