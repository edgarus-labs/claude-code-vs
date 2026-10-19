using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Represents a wrapper around a TaskCompletionSource&lt;T&gt; that provides thread-safe access to its TrySetResult and TrySetException methods.
/// </summary>
public sealed class TaskCompletionSourceSlot<T>
{
    private readonly System.Threading.Tasks.TaskCompletionSource<T> _tcs =
        new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the task.
    /// </summary>
    public System.Threading.Tasks.Task<T> Task => _tcs.Task;

    /// <summary>
    /// Attempts to set the result of the task completion source to the specified value if it has not already been set.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>true if the condition is met; otherwise, false.</returns>
    public bool TrySetResult(T value) => _tcs.TrySetResult(value);

    /// <summary>
    /// Attempts to set the exception on the underlying TaskCompletionSource, returning true if successful.
    /// </summary>
    /// <param name="ex">The ex.</param>
    /// <returns>true if the condition is met; otherwise, false.</returns>
    public bool TrySetException(Exception ex) => _tcs.TrySetException(ex);
}
