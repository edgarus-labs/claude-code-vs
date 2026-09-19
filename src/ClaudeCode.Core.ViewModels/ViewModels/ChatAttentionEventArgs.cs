using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>Something happened that the user may want to come back for: a finished turn, a pending
/// permission, or a plan awaiting review. Hosts surface it (e.g. a system notification) when the
/// IDE is not in the foreground.</summary>
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
