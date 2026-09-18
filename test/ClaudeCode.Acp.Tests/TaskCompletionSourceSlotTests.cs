using ClaudeCode.Contracts;
using System;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// TaskCompletionSourceSlot{T}.SetResult/SetException were misleadingly named after
/// TaskCompletionSource.SetResult/SetException (which throw on an already-completed task) while
/// actually behaving like TrySetResult/TrySetException (silently ignoring a second completion). The
/// rename makes the name match the real, already-safe semantics; behavior is unchanged.
/// </summary>
public sealed class TaskCompletionSourceSlotTests
{
    [Fact]
    public async Task TrySetResult_FirstCall_ReturnsTrueAndCompletesTheTask()
    {
        var slot = new TaskCompletionSourceSlot<string>();

        bool succeeded = slot.TrySetResult("value");

        Assert.True(succeeded);
        Assert.Equal("value", await slot.Task);
    }

    [Fact]
    public async Task TrySetResult_AfterAlreadyCompleted_ReturnsFalse_AndDoesNotThrowOrChangeTheResult()
    {
        var slot = new TaskCompletionSourceSlot<string>();
        slot.TrySetResult("first");

        bool succeeded = slot.TrySetResult("second");

        Assert.False(succeeded);
        Assert.Equal("first", await slot.Task);
    }

    [Fact]
    public async Task TrySetException_FirstCall_ReturnsTrueAndFaultsTheTask()
    {
        var slot = new TaskCompletionSourceSlot<string>();

        bool succeeded = slot.TrySetException(new InvalidOperationException("boom"));

        Assert.True(succeeded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => slot.Task);
    }

    [Fact]
    public async Task TrySetException_AfterAlreadyCompleted_ReturnsFalse_AndDoesNotThrowOrChangeTheResult()
    {
        var slot = new TaskCompletionSourceSlot<string>();
        slot.TrySetResult("already done");

        bool succeeded = slot.TrySetException(new InvalidOperationException("too late"));

        Assert.False(succeeded);
        Assert.Equal("already done", await slot.Task);
    }
}
