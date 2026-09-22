using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

/// <summary>Waits for a spawned process to exit, or for cancellation, whichever comes first.</summary>
public static class ProcessExitWait
{
    /// <summary>
    /// Completes when <paramref name="exited"/> does, or throws <see cref="OperationCanceledException"/>
    /// as soon as <paramref name="cancellationToken"/> is cancelled - even if the process never exits
    /// (a failed kill must not hang the caller). The cancellation callback only signals; the waiter's
    /// continuation (typically process-tree cleanup) never runs inline on the thread that called
    /// <c>Cancel()</c>, which is usually the UI thread.
    /// </summary>
    public static async Task WaitForExitAsync(Task exited, CancellationToken cancellationToken)
    {
        if (exited is null) throw new ArgumentNullException(nameof(exited));
        cancellationToken.ThrowIfCancellationRequested();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            await Task.WhenAny(exited, cancelled.Task).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
