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

    public ChatAttentionKind Kind { get; }

    public string Title { get; }

    public string Message { get; }
}

public enum ChatAttentionKind { TurnCompleted, PermissionNeeded, PlanReview }
