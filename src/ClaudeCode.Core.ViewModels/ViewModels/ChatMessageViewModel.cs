using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

public sealed class ChatMessageViewModel : ObservableObject
{
    /// <param name="isPending">True for a message queued locally while an earlier turn is still in
    /// flight; see <see cref="IsPending"/>. Only a <see cref="ChatRole.User"/> message is ever
    /// queued, because only the composer can produce one.</param>
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

    private readonly StringBuilder _textBuilder;
    private string? _text;
    private bool _isTruncated;
    private bool _isPending;

    /// <summary>True while this message is queued locally - typed and sent while a previous turn was
    /// still in flight - and the agent has not started on it yet (it may already be waiting in the
    /// agent's own prompt queue). The transcript dims a pending
    /// bubble so it cannot be mistaken for one that was delivered.</summary>
    public bool IsPending
    {
        get => _isPending;
        private set => SetProperty(ref _isPending, value);
    }

    /// <summary>Records that this message no longer waits to go out: the agent is running it, or
    /// has answered it. One-way on purpose:
    /// a message that has gone out can never become pending again, so the flag is not a setter
    /// anyone outside can flip back.</summary>
    public void MarkSent() => IsPending = false;

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

    private int _thinkingLength;

    /// <summary>Bumped on every thought appended, so the transcript repaints (see
    /// TranscriptHostProtocol.AffectsTranscript); the thinking itself lives in <see cref="Parts"/>.</summary>
    public int ThinkingVersion { get; private set; }

    /// <summary>Appends a chunk of Claude's thinking to the ordered sequence - see
    /// <see cref="ChatThinkingPart"/>. Agent-supplied, so it has the same total bound as the reply's
    /// own text.</summary>
    public void AppendThought(string chunk)
    {
        if (string.IsNullOrEmpty(chunk) || _thinkingLength >= MarkdownSafetyLimits.MaxMarkdownLength)
        {
            return;
        }

        var appended = chunk.Substring(0, Math.Min(chunk.Length, MarkdownSafetyLimits.MaxMarkdownLength - _thinkingLength));
        _thinkingLength += appended.Length;
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
