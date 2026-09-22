using ClaudeCode.Acp;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class ProcessExitWaitTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReturnsOnceTheProcessExits()
    {
        var exited = new TaskCompletionSource<bool>();
        var waiting = ProcessExitWait.WaitForExitAsync(exited.Task, CancellationToken.None);

        exited.SetResult(true);

        await waiting.WaitAsync(Guard);
    }

    [Fact]
    public async Task Cancellation_EndsTheWait_EvenIfTheProcessNeverExits()
    {
        // Models a kill that failed: the exit signal never arrives.
        var neverExits = new TaskCompletionSource<bool>();
        using var cts = new CancellationTokenSource();
        var waiting = ProcessExitWait.WaitForExitAsync(neverExits.Task, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Guard));
    }

    [Fact]
    public async Task AlreadyCancelled_Throws()
    {
        var neverExits = new TaskCompletionSource<bool>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessExitWait.WaitForExitAsync(neverExits.Task, new CancellationToken(true)).WaitAsync(Guard));
    }

    [Fact]
    public async Task Cancel_NeverRunsTheWaitersContinuationOnTheCancellingThread()
    {
        // The caller cancels from the UI thread; whatever follows the wait (process-tree cleanup)
        // must not run inline there.
        var neverExits = new TaskCompletionSource<bool>();
        using var cts = new CancellationTokenSource();
        int continuationThread = -1;
        var waiting = Task.Run(async () =>
        {
            try { await ProcessExitWait.WaitForExitAsync(neverExits.Task, cts.Token); }
            catch (OperationCanceledException) { continuationThread = Environment.CurrentManagedThreadId; }
        });
        await Task.Delay(50);

        int cancellingThread = Environment.CurrentManagedThreadId;
        cts.Cancel();
        await waiting.WaitAsync(Guard);

        Assert.NotEqual(cancellingThread, continuationThread);
    }
}
