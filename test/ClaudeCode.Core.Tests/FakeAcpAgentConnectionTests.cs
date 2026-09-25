using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels.Demo;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

// The demo connection is a production fallback for IAcpAgentConnection, so it must end a turn the
// way AcpProcessConnection does: "cancelled" only when CancelAsync stopped it, while a cancelled
// caller token surfaces as OperationCanceledException.
public sealed class FakeAcpAgentConnectionTests
{
    private static readonly ContentBlock[] Prompt = [new ContentBlock.Text("hello there")];

    [Fact]
    public async Task SendPromptAsync_CallerTokenCancelled_ThrowsOperationCanceled()
    {
        await using var connection = new FakeAcpAgentConnection(TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();

        var turn = connection.SendPromptAsync("s1", Prompt, caller.Token);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SendPromptAsync_StoppedByCancelAsync_ReturnsCancelled()
    {
        await using var connection = new FakeAcpAgentConnection(TimeSpan.FromSeconds(30));

        var turn = connection.SendPromptAsync("s1", Prompt, CancellationToken.None);
        await connection.CancelAsync("s1", CancellationToken.None);

        Assert.Equal("cancelled", await turn.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
