using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class FileWriteRequestEventArgs : EventArgs
{
    public FileWriteRequestEventArgs(string path, string content) { Path = path; Content = content; }

    public string Path { get; }

    public string Content { get; }

    public TaskCompletionSourceSlot<bool> Response { get; } = new TaskCompletionSourceSlot<bool>();
}
