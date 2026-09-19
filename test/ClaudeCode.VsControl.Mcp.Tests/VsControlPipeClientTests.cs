using ClaudeCode.Contracts;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.VsControl.Mcp.Tests;

public sealed class VsControlPipeClientTests
{
    private static readonly JsonSerializerOptions _wireOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("token\nrequest")]
    [InlineData("token\rrequest")]
    public void Constructor_RejectsMissingOrMultilineHandshakeToken(string token)
    {
        Assert.Throws<ArgumentException>(() => new VsControlPipeClient("unused", handshakeToken: token));
    }

    [Fact]
    public async Task SendAsync_CancelledPartialRequest_ClosesTransportAndRetryHandshakesAgain()
    {
        string pipeName = $"vscontrol-cancel-request-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource();
        await using var client = new VsControlPipeClient(pipeName, handshakeToken: "token");
        Task connected = server.WaitForConnectionAsync();
        Task<VsControlResponse> attempt = client.SendAsync(new VsControlRequest
        {
            Id = "partial",
            Method = "getSolutionInfo",
            ParamsJson = new string('x', 8 * 1024 * 1024),
        }, cancellation.Token);
        try
        {
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            using (var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true))
            {
                Assert.Equal("token", await reader.ReadLineAsync());
                Assert.Equal(1, await reader.ReadAsync(new char[1]));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(2)));
                await server.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(2));
            }
            server.Disconnect();
            Task retryServer = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                Assert.Equal("token", await reader.ReadLineAsync());
                var request = JsonSerializer.Deserialize<VsControlRequest>((await reader.ReadLineAsync())!, _wireOptions)!;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new VsControlResponse { Id = request.Id, ResultJson = "{}" }, _wireOptions));
            });
            var result = await client.SendAsync(new VsControlRequest { Id = "retry", Method = "getSolutionInfo" }, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await retryServer.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(result.Error);
            Assert.Equal("{}", result.ResultJson);
        }
        finally
        {
            server.Dispose();
            _ = await Record.ExceptionAsync(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task DisposeAsync_DuringBackpressuredHandshake_CancelsSendAndClosesPeer()
    {
        string pipeName = $"vscontrol-dispose-handshake-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new VsControlPipeClient(pipeName, handshakeToken: new string('t', 8 * 1024 * 1024));
        Task connected = server.WaitForConnectionAsync();
        Task<VsControlResponse> attempt = client.SendAsync(
            new VsControlRequest { Id = "disposed", Method = "getSolutionInfo" }, CancellationToken.None);
        await connected.WaitAsync(TimeSpan.FromSeconds(5));
        var firstByte = new byte[1];
        Assert.Equal(1, await server.ReadAsync(firstByte));
        Task<VsControlResponse> waitingAttempt = client.SendAsync(
            new VsControlRequest { Id = "waiting", Method = "getSolutionInfo" }, CancellationToken.None);
        try
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitingAttempt.WaitAsync(TimeSpan.FromSeconds(2)));
            await server.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync(
                new VsControlRequest { Id = "after-dispose", Method = "getSolutionInfo" }, CancellationToken.None));
        }
        finally
        {
            server.Dispose();
            _ = await Record.ExceptionAsync(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(2)));
            _ = await Record.ExceptionAsync(async () => await waitingAttempt.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task SendAsync_CancelledHandshakeClosesConnection_AndRetryStartsWithToken()
    {
        string pipeName = $"vscontrol-cancel-handshake-{Guid.NewGuid():N}";
        string token = new string('t', 8 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        using var stopFirstServer = new SemaphoreSlim(0, 1);
        using var firstServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task firstConnection = Task.Run(async () =>
        {
            await firstServer.WaitForConnectionAsync();
            var firstByte = new byte[1];
            Assert.Equal(1, await firstServer.ReadAsync(firstByte));
            cancellation.Cancel();
            await stopFirstServer.WaitAsync();
            firstServer.Disconnect();
        });
        await using var client = new VsControlPipeClient(pipeName, handshakeToken: token);
        Task<VsControlResponse> attempt = client.SendAsync(
            new VsControlRequest { Id = "cancelled", Method = "getSolutionInfo" }, cancellation.Token);
        Exception? failure;
        try
        {
            failure = await Record.ExceptionAsync(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            stopFirstServer.Release();
            await firstConnection;
            _ = await Record.ExceptionAsync(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Assert.IsAssignableFrom<OperationCanceledException>(failure);

        Task secondConnection = Task.Run(async () =>
        {
            await firstServer.WaitForConnectionAsync();
            using var reader = new StreamReader(firstServer, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(firstServer, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            Assert.Equal(token, await reader.ReadLineAsync());
            var request = JsonSerializer.Deserialize<VsControlRequest>((await reader.ReadLineAsync())!, _wireOptions)!;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new VsControlResponse { Id = request.Id, ResultJson = "{}" }, _wireOptions));
        });
        var response = await client.SendAsync(new VsControlRequest { Id = "retry", Method = "getSolutionInfo" }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await secondConnection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(response.Error);
        Assert.Equal("{}", response.ResultJson);
    }

    [Fact]
    public async Task SendAsync_WhenCallerCancelsPendingRequest_ThrowsCancellation()
    {
        string pipeName = $"vscontrol-caller-cancel-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource();
        await using var client = new VsControlPipeClient(pipeName, handshakeToken: "token");
        Task connected = server.WaitForConnectionAsync();
        var attempt = client.SendAsync(new VsControlRequest { Id = "cancel", Method = "getSolutionInfo" }, cancellation.Token);
        await connected;
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        Assert.Equal("token", await reader.ReadLineAsync());
        Assert.NotNull(await reader.ReadLineAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SendAsync_WritesHandshakeTokenAsFirstLine_BeforeTheRequestLine()
    {
        string pipeName = $"vscontrol-handshake-ok-{Guid.NewGuid():N}";
        const string expectedToken = "correct-handshake-token";
        using var serverStarted = new SemaphoreSlim(0, 1);
        string? observedToken = null;

        Task serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            serverStarted.Release();
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            observedToken = await reader.ReadLineAsync();

            string? requestLine = await reader.ReadLineAsync();
            Assert.NotNull(requestLine);
            var request = JsonSerializer.Deserialize<VsControlRequest>(requestLine!, _wireOptions);
            Assert.NotNull(request);

            var response = new VsControlResponse { Id = request!.Id, ResultJson = "{}" };
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, _wireOptions));
        });

        await serverStarted.WaitAsync();

        await using var client = new VsControlPipeClient(pipeName, connectTimeout: TimeSpan.FromSeconds(5), handshakeToken: expectedToken);
        var result = await client.SendAsync(new VsControlRequest { Id = "1", Method = "getSolutionInfo", ParamsJson = "{}" }, CancellationToken.None);

        await serverTask;

        Assert.Equal(expectedToken, observedToken);
        Assert.Null(result.Error);
        Assert.Equal("{}", result.ResultJson);
    }

    [Fact]
    public async Task SendAsync_WhenServerRejectsHandshakeAndClosesTheConnection_SurfacesAnErrorInsteadOfHanging()
    {
        string pipeName = $"vscontrol-handshake-rejected-{Guid.NewGuid():N}";
        using var serverStarted = new SemaphoreSlim(0, 1);

        Task serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            serverStarted.Release();
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            _ = await reader.ReadLineAsync(); // read (and reject) the handshake token line

            // Mismatched token: close without ever reading/answering the request line, mirroring
            // VsControlPipeServer's handshake rejection behavior.
        });

        await serverStarted.WaitAsync();

        await using var client = new VsControlPipeClient(
            pipeName,
            connectTimeout: TimeSpan.FromSeconds(5),
            requestTimeout: TimeSpan.FromSeconds(5),
            handshakeToken: "whatever-token");

        VsControlResponse? result = null;
        Exception? thrown = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result = await client.SendAsync(new VsControlRequest { Id = "1", Method = "getSolutionInfo", ParamsJson = "{}" }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        sw.Stop();

        await serverTask;

        // The server dropped the connection right after rejecting the handshake token, without ever
        // processing the request: the client must observe an explicit failure quickly - either a
        // thrown exception or an error-carrying response - never a hang.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Expected the failure to surface quickly; took {sw.Elapsed}.");
        Assert.True(thrown is not null || !string.IsNullOrEmpty(result?.Error), "Expected either a thrown exception or an error response.");
    }

    [Fact]
    public async Task SendAsync_WhenServerNeverResponds_TimesOutWithAnErrorResponse_InsteadOfHangingForever()
    {
        string pipeName = $"vscontrol-noresponse-{Guid.NewGuid():N}";
        using var serverStarted = new SemaphoreSlim(0, 1);
        using var serverShouldExit = new SemaphoreSlim(0, 1);

        Task serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            serverStarted.Release();
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            _ = await reader.ReadLineAsync(); // handshake token
            _ = await reader.ReadLineAsync(); // request line

            // Deliberately never write a response; keep the pipe open until the test is done asserting.
            await serverShouldExit.WaitAsync();
        });

        await serverStarted.WaitAsync();

        await using var client = new VsControlPipeClient(
            pipeName,
            connectTimeout: TimeSpan.FromSeconds(5),
            requestTimeout: TimeSpan.FromMilliseconds(100),
            handshakeToken: "token");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await client.SendAsync(new VsControlRequest { Id = "1", Method = "getSolutionInfo", ParamsJson = "{}" }, CancellationToken.None);
        sw.Stop();

        serverShouldExit.Release();
        await serverTask;

        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Expected the request timeout to fire quickly; took {sw.Elapsed}.");
    }

    [Theory]
    [InlineData("buildSolution")]
    [InlineData("buildProject")]
    [InlineData("startDebugging")]
    public async Task SendAsync_ForAMethodThatCompiles_UsesTheLongerBuildTimeout_NotTheDefaultRequestTimeout(string method)
    {
        string pipeName = $"vscontrol-buildtimeout-{Guid.NewGuid():N}";
        using var serverStarted = new SemaphoreSlim(0, 1);
        using var serverShouldExit = new SemaphoreSlim(0, 1);

        Task serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            serverStarted.Release();
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            _ = await reader.ReadLineAsync(); // handshake token
            string? requestLine = await reader.ReadLineAsync();
            var request = JsonSerializer.Deserialize<VsControlRequest>(requestLine!, _wireOptions);

            // Respond just after the short default request timeout would have fired, but well within
            // the longer build timeout: proves every method that compiles actually gets the longer budget.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            var response = new VsControlResponse { Id = request!.Id, ResultJson = """{"succeeded":true,"errorCount":0,"warningCount":0}""" };
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, _wireOptions));

            await serverShouldExit.WaitAsync();
        });

        await serverStarted.WaitAsync();

        await using var client = new VsControlPipeClient(
            pipeName,
            connectTimeout: TimeSpan.FromSeconds(5),
            requestTimeout: TimeSpan.FromMilliseconds(50),
            buildTimeout: TimeSpan.FromSeconds(5),
            handshakeToken: "token");

        var result = await client.SendAsync(new VsControlRequest { Id = "1", Method = method, ParamsJson = "{}" }, CancellationToken.None);

        serverShouldExit.Release();
        await serverTask;

        Assert.Null(result.Error);
        Assert.Contains("succeeded", result.ResultJson);
    }

    [Fact]
    public async Task ReadLoopTeardown_WhileARequestWriteIsStillInFlight_DoesNotFaultDisposal()
    {
        string pipeName = $"vscontrol-teardown-race-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new VsControlPipeClient(pipeName, handshakeToken: "token");
        Task connected = server.WaitForConnectionAsync();
        Task<VsControlResponse> attempt = client.SendAsync(new VsControlRequest
        {
            Id = "in-flight",
            Method = "getSolutionInfo",
            ParamsJson = new string('x', 8 * 1024 * 1024),
        }, CancellationToken.None);
        try
        {
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            using (var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true))
            {
                Assert.Equal("token", await reader.ReadLineAsync());
                // One char of the request read, the rest backpressured: the write is now in flight.
                Assert.Equal(1, await reader.ReadAsync(new char[1]));
            }

            // The VS host vanishes mid-request, so the read loop tears the connection down while
            // that write is still running. Teardown must join the write instead of disposing the
            // writer underneath it - otherwise the read loop faults and disposal rethrows.
            server.Disconnect();

            var disposeFailure = await Record.ExceptionAsync(
                async () => await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(disposeFailure);
        }
        finally
        {
            _ = await Record.ExceptionAsync(async () => await attempt.WaitAsync(TimeSpan.FromSeconds(5)));
            server.Dispose();
        }
    }
}
