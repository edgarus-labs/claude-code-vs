using System;

namespace ClaudeCode.Contracts;

public sealed class FileReadRequestEventArgs : EventArgs
{
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
