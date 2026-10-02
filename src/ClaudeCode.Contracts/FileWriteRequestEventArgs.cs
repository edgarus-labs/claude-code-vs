using System;

namespace ClaudeCode.Contracts;

public sealed class FileWriteRequestEventArgs : EventArgs
{
    public FileWriteRequestEventArgs(string path, string content) { Path = path; Content = content; }

    /// <summary>
    /// Gets the path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the content.
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// Gets the response.
    /// </summary>
    public TaskCompletionSourceSlot<bool> Response { get; } = new TaskCompletionSourceSlot<bool>();
}
