using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

public sealed class ChatMessageViewModel : ObservableObject
{
    /// <param name="isPending">True for a message sent while an earlier turn is still in
    /// flight; see <see cref="IsPending"/>.</param>
    public ChatMessageViewModel(ChatRole role, string text = "", bool isPending = false)
    {
        Role = role;
        _isPending = isPending;
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

    private static int _nextId;

    /// <summary>Identifier unique for the life of the process.</summary>
    public int Id { get; } = System.Threading.Interlocked.Increment(ref _nextId);

    private readonly StringBuilder _textBuilder;
    private string? _text;
    private bool _isTruncated;
    private bool _isPending;

    /// <summary>True while the agent has not started on this message yet: it was sent while a
    /// previous turn was still in flight and is held locally or waiting in the agent's prompt queue.</summary>
    public bool IsPending
    {
        get => _isPending;
        private set => SetProperty(ref _isPending, value);
    }

    /// <summary>Records that this message no longer waits to go out. A sent message never becomes
    /// pending again.</summary>
    public void MarkSent() => IsPending = false;

    public string Text => _text ??= _textBuilder.ToString();

    private int? _durationSeconds;

    /// <summary>How long this turn took, in seconds, set once when it ends.</summary>
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

    /// <summary>Appends a new tool call to the ordered sequence. Updates to an existing tool call
    /// mutate the <see cref="ToolCallCardViewModel"/> already referenced by its part.</summary>
    public void AppendToolCall(ToolCallCardViewModel card) => Parts.Add(new ChatToolCallPart(card));

    private int _thinkingLength;

    /// <summary>Incremented on every thought appended; the thinking itself lives in <see cref="Parts"/>.</summary>
    public int ThinkingVersion { get; private set; }

    /// <summary>Appends a chunk of Claude's thinking to the ordered sequence - see
    /// <see cref="ChatThinkingPart"/>. Bounded by the same total length as the reply's own text.</summary>
    public void AppendThought(string chunk)
    {
        if (string.IsNullOrEmpty(chunk) || _thinkingLength >= MarkdownSafetyLimits.MaxMarkdownLength)
        {
            return;
        }

        var remaining = MarkdownSafetyLimits.MaxMarkdownLength - _thinkingLength;
        var appended = chunk.Substring(0, Math.Min(chunk.Length, remaining));
        _thinkingLength += appended.Length;
        if (chunk.Length > remaining) appended += MarkdownSafetyLimits.TruncationNotice;
        if (Parts.Count > 0 && Parts[Parts.Count - 1] is ChatThinkingPart lastThought)
        {
            lastThought.Append(appended);
        }
        else
        {
            var part = new ChatThinkingPart();
            part.Append(appended);
            Parts.Add(part);
        }

        ThinkingVersion++;
        OnPropertyChanged(nameof(ThinkingVersion));
    }
}
