using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>Something happened that the user may want to come back for: a finished turn, a pending
/// permission, or a plan awaiting review. Hosts surface it (e.g. a system notification) when the
/// IDE is not in the foreground.</summary>
public sealed class ChatAttentionEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the ChatAttentionEventArgs class with the specified attention kind, title, and message.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="title">The title.</param>
    /// <param name="message">The message.</param>
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

/// <summary>
/// Specifies the categories of attention signals that require user notification or action in a chat session, such as a completed turn, a pending permission request, or a plan awaiting review.
/// </summary>
public enum ChatAttentionKind { TurnCompleted, PermissionNeeded, PlanReview }
