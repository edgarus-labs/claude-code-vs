using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts
{
    /// <summary>A block of prompt/response content exchanged over ACP (text, image, or an embedded resource such as a file reference).</summary>
    public abstract class ContentBlock
    {
        public sealed class Text : ContentBlock
        {
            public Text(string text) => Value = text;
            public string Value { get; }
        }

        public sealed class Image : ContentBlock
        {
            public Image(string mimeType, string base64Data) { MimeType = mimeType; Base64Data = base64Data; }
            public string MimeType { get; }
            public string Base64Data { get; }
        }

        public sealed class ResourceLink : ContentBlock
        {
            public ResourceLink(string uri, string? name) { Uri = uri; Name = name; }
            public string Uri { get; }
            public string? Name { get; }
        }
    }

    public enum ToolCallStatus { Pending, InProgress, Completed, Failed }

    public sealed class ToolCallUpdate
    {
        public string ToolCallId { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Kind { get; set; }
        public ToolCallStatus Status { get; set; }
        public IReadOnlyList<ToolCallContent> Content { get; set; } = Array.Empty<ToolCallContent>();
    }

    /// <summary>Content attached to a tool call, e.g. a diff to render or a plain text result.</summary>
    public sealed class ToolCallContent
    {
        public string? Text { get; set; }
        public string? Path { get; set; }
        public string? OldText { get; set; }
        public string? NewText { get; set; }
        public bool IsDiff => Path != null && NewText != null;
    }

    public enum PlanEntryStatus { Pending, InProgress, Completed }

    public sealed class PlanEntry
    {
        public string Content { get; set; } = "";
        public PlanEntryStatus Status { get; set; }
    }

    /// <summary>One incremental `session/update` notification pushed by the agent while a turn is running.</summary>
    public abstract class SessionUpdate
    {
        public sealed class AgentMessageChunk : SessionUpdate
        {
            public AgentMessageChunk(string text) => Text = text;
            public string Text { get; }
        }

        public sealed class AgentThoughtChunk : SessionUpdate
        {
            public AgentThoughtChunk(string text) => Text = text;
            public string Text { get; }
        }

        public sealed class ToolCall : SessionUpdate
        {
            public ToolCall(ToolCallUpdate call) => Call = call;
            public ToolCallUpdate Call { get; }
        }

        public sealed class Plan : SessionUpdate
        {
            public Plan(IReadOnlyList<PlanEntry> entries) => Entries = entries;
            public IReadOnlyList<PlanEntry> Entries { get; }
        }

        public sealed class TurnEnded : SessionUpdate
        {
            public TurnEnded(string stopReason) => StopReason = stopReason;
            public string StopReason { get; }
        }
    }

    public sealed class SessionUpdateEventArgs : EventArgs
    {
        public SessionUpdateEventArgs(string sessionId, SessionUpdate update) { SessionId = sessionId; Update = update; }
        public string SessionId { get; }
        public SessionUpdate Update { get; }
    }

    public enum PermissionOutcome { AllowOnce, AllowAlways, RejectOnce, RejectAlways, Cancelled }

    public sealed class PermissionOption
    {
        public string OptionId { get; set; } = "";
        public string Label { get; set; } = "";
        public PermissionOutcome Outcome { get; set; }
    }

    /// <summary>Raised when the agent asks for user consent before a side-effecting tool call. Handlers MUST set <see cref="Response"/>.</summary>
    public sealed class PermissionRequestEventArgs : EventArgs
    {
        public PermissionRequestEventArgs(string sessionId, ToolCallUpdate call, IReadOnlyList<PermissionOption> options)
        {
            SessionId = sessionId;
            Call = call;
            Options = options;
        }
        public string SessionId { get; }
        public ToolCallUpdate Call { get; }
        public IReadOnlyList<PermissionOption> Options { get; }

        /// <summary>Handler MUST complete this with the chosen option id before returning.</summary>
        public TaskCompletionSourceSlot<string> Response { get; } = new TaskCompletionSourceSlot<string>();
    }

    public sealed class FileReadRequestEventArgs : EventArgs
    {
        public FileReadRequestEventArgs(string path, int? line, int? limit) { Path = path; Line = line; Limit = limit; }
        public string Path { get; }
        public int? Line { get; }
        public int? Limit { get; }
        public TaskCompletionSourceSlot<string> Response { get; } = new TaskCompletionSourceSlot<string>();
    }

    public sealed class FileWriteRequestEventArgs : EventArgs
    {
        public FileWriteRequestEventArgs(string path, string content) { Path = path; Content = content; }
        public string Path { get; }
        public string Content { get; }
        public TaskCompletionSourceSlot<bool> Response { get; } = new TaskCompletionSourceSlot<bool>();
    }

    /// <summary>Minimal boxed TaskCompletionSource wrapper so contracts stay dependency-free of async plumbing choices.</summary>
    public sealed class TaskCompletionSourceSlot<T>
    {
        private readonly System.Threading.Tasks.TaskCompletionSource<T> _tcs =
            new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Threading.Tasks.Task<T> Task => _tcs.Task;
        public void SetResult(T value) => _tcs.TrySetResult(value);
        public void SetException(Exception ex) => _tcs.TrySetException(ex);
    }
}
