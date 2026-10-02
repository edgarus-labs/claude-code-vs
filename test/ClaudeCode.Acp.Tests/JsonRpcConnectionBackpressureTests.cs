using System;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// Covers <c>DisposeAsync</c> unblocking a write blocked on pipe backpressure through cancellation,
/// even when closing the stream does not unblock it, and a write finishing after disposal reporting
/// cancellation rather than a disposal fault.
/// </summary>
public sealed class JsonRpcConnectionBackpressureTests
{
    [Fact]
    public async Task DisposeAsync_WriteBlockedOnBackpressure_UnblocksViaCancellation_EvenWhenClosingTheStreamDoesNotUnblockIt()
    {
        var toTest = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        var fromTest = new Pipe();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), new NonDisposingStream(toTest.Writer.AsStream()));
        connection.Start();

        Task<JsonNode?> pendingSend = connection.SendRequestAsync("first", new JsonObject(), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(pendingSend.IsCompleted, "the write should be blocked on backpressure before DisposeAsync runs.");

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Task settledOrTimedOut = await Task.WhenAny(pendingSend, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(ReferenceEquals(settledOrTimedOut, pendingSend),
            "the blocked write must be unblocked by disposal (via cancellation) instead of hanging forever.");
        await Assert.ThrowsAnyAsync<Exception>(() => pendingSend);
    }

    [Fact]
    public async Task DisposeAsync_WriteFinishesAfterDisposal_PreservesCancellationInsteadOfDisposalFault()
    {
        var fromTest = new Pipe();
        using var output = new DelayedWriteStream();
        var connection = new JsonRpcConnection(fromTest.Reader.AsStream(), output);
        Task notification = connection.SendNotificationAsync("test", null, CancellationToken.None);

        try
        {
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            output.Release.TrySetResult(true);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            output.Release.TrySetResult(true);
            await connection.DisposeAsync();
        }
    }

    private sealed class DelayedWriteStream : MemoryStream
    {
        internal TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class NonDisposingStream : Stream
    {
        private readonly Stream _inner;

        public NonDisposingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
