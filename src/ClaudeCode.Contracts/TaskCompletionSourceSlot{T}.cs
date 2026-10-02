using System;

namespace ClaudeCode.Contracts;

public sealed class TaskCompletionSourceSlot<T>
{
    private readonly System.Threading.Tasks.TaskCompletionSource<T> _tcs =
        new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the task.
    /// </summary>
    public System.Threading.Tasks.Task<T> Task => _tcs.Task;

    public bool TrySetResult(T value) => _tcs.TrySetResult(value);

    public bool TrySetException(Exception ex) => _tcs.TrySetException(ex);
}
