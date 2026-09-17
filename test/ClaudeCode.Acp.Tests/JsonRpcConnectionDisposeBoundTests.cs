using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// DisposeAsync used to await the pump task with no time bound. If a Disconnected subscriber blocks
/// synchronously (the pump's finally block invokes it inline), DisposeAsync could hang forever
/// instead of eventually giving up and completing disposal anyway.
/// </summary>
public sealed class JsonRpcConnectionDisposeBoundTests
{
    [Fact]
    public async Task DisposeAsync_DisconnectedSubscriberBlocksSynchronously_DoesNotHangForever()
    {
        var toTest = new Pipe();
        var fromTest = new Pipe();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), toTest.Writer.AsStream());
        connection.Start();

        var release = new ManualResetEventSlim(false);
        connection.Disconnected += (_, _) => release.Wait(); // simulates a badly-behaved subscriber.

        fromTest.Writer.Complete(); // clean EOF -> the pump's finally block invokes Disconnected inline.

        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set(); // let the blocked pump thread go so it doesn't leak past this test.
        }
    }
}
