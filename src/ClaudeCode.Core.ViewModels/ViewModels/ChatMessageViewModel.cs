using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
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
        if (_text.Length > 0)
        {
            Parts.Add(new ChatTextPart(_text));
        }
    }

    public ChatRole Role { get; }

    private bool _isPending;

    /// <summary>True while this user message is queued locally - typed and sent while a previous
    /// turn was still in flight - and hasn't actually gone out to the agent yet. Cleared once the
    /// prior turn finishes and this message is dispatched.</summary>
    public bool IsPending
    {
        get => _isPending;
        set => SetProperty(ref _isPending, value);
    }

    private readonly StringBuilder _textBuilder;
    private string? _text;
    private bool _isTruncated;

    public string Text => _text ??= _textBuilder.ToString();

    private int? _durationSeconds;

    /// <summary>How long this turn took, set once when it ends. Per-turn cost would belong here too,
    /// but the ACP adapter this extension talks to (@agentclientprotocol/claude-agent-acp, an
    /// external npm package) reads the SDK "result" message's cost/usage and does not forward it -
    /// only `stopReason` survives into the ACP response. Context tokens arrive separately as
    /// <c>usage_update</c> (see <see cref="TokensUsed"/>).</summary>
    public int? DurationSeconds
    {
        get => _durationSeconds;
        set => SetProperty(ref _durationSeconds, value);
    }

    private IReadOnlyList<ChatMessageImage> _images = Array.Empty<ChatMessageImage>();

    /// <summary>Images the user sent with this message (rendered as thumbnails in the transcript).</summary>
    public IReadOnlyList<ChatMessageImage> Images
    {
        get => _images;
        set => SetProperty(ref _images, value);
    }

    private long? _tokensUsed;

    /// <summary>Context tokens the turn that produced this message consumed (from ACP usage_update), if reported.</summary>
    public long? TokensUsed
    {
        get => _tokensUsed;
        set => SetProperty(ref _tokensUsed, value);
    }

    public ObservableCollection<ToolCallCardViewModel> ToolCalls { get; } = new ObservableCollection<ToolCallCardViewModel>();

    /// <summary>Text and tool calls in the order they actually happened - see <see cref="ChatMessagePart"/>.</summary>
    public ObservableCollection<ChatMessagePart> Parts { get; } = new ObservableCollection<ChatMessagePart>();

    public void AppendText(string chunk)
    {
        if (_isTruncated || string.IsNullOrEmpty(chunk))
        {
            return;
        }

        var remaining = MarkdownSafetyLimits.MaxMarkdownLength - _textBuilder.Length;
        var appended = chunk.Substring(0, Math.Min(chunk.Length, remaining));
        _textBuilder.Append(appended);
        if (chunk.Length > remaining)
        {
            _textBuilder.Append(MarkdownSafetyLimits.TruncationNotice);
            appended += MarkdownSafetyLimits.TruncationNotice;
            _isTruncated = true;
        }

        if (appended.Length > 0)
        {
            if (Parts.Count > 0 && Parts[Parts.Count - 1] is ChatTextPart lastText)
            {
                lastText.Append(appended);
            }
            else
            {
                Parts.Add(new ChatTextPart(appended));
            }
        }

        _text = null;
        OnPropertyChanged(nameof(Text));
    }

    /// <summary>Appends a tool call to the ordered sequence. Only for a *new* tool call (the first
    /// time this ToolCallId is seen) - updates to an existing one mutate the same
    /// <see cref="ToolCallCardViewModel"/> instance already referenced by its part, so they need no
    /// separate Parts entry.</summary>
    public void AppendToolCall(ToolCallCardViewModel card) => Parts.Add(new ChatToolCallPart(card));
}
