using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Represents an event argument containing file read request details including path, line number, limit, and response task completion source.
/// </summary>
public sealed class FileReadRequestEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the FileReadRequestEventArgs class with the specified file path, line number, and read limit.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="line">The line.</param>
    /// <param name="limit">The limit.</param>
    public FileReadRequestEventArgs(string path, int? line, int? limit) { Path = path; Line = line; Limit = limit; }

    /// <summary>
    /// Gets the path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the line.
    /// </summary>
    public int? Line { get; }

    /// <summary>
    /// Gets the limit.
    /// </summary>
    public int? Limit { get; }

    /// <summary>
    /// Gets the response.
    /// </summary>
    public TaskCompletionSourceSlot<string> Response { get; } = new TaskCompletionSourceSlot<string>();
}
