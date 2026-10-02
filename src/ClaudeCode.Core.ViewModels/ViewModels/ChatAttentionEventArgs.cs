using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>Describes an event the user may want to return for: a finished turn, a pending
/// permission, or a plan awaiting review.</summary>
public sealed class ChatAttentionEventArgs : EventArgs
{
    public ChatAttentionEventArgs(ChatAttentionKind kind, string title, string message)
    {
        Kind = kind;
        Title = title;
        Message = message;
    }

    /// <summary>
    /// Gets the kind.
    /// </summary>
    public ChatAttentionKind Kind { get; }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; }
}
