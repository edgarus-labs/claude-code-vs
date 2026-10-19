using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Represents an event argument containing file path, content, and a task completion source for asynchronous file write operations.
/// </summary>
public sealed class FileWriteRequestEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the FileWriteRequestEventArgs class with the specified file path and content.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="content">The content.</param>
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
