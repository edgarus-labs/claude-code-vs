using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

public sealed class FileReadRequestEventArgs : EventArgs
{
    public FileReadRequestEventArgs(string path, int? line, int? limit) { Path = path; Line = line; Limit = limit; }

    public string Path { get; }

    public int? Line { get; }

    public int? Limit { get; }

    public TaskCompletionSourceSlot<string> Response { get; } = new TaskCompletionSourceSlot<string>();
}
