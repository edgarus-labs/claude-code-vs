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

    [Fact]
    public async Task SendAsync_ForBuildSolution_UsesTheLongerBuildTimeout_NotTheDefaultRequestTimeout()
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
            // the longer build timeout: proves buildSolution actually gets the longer budget.
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

        var result = await client.SendAsync(new VsControlRequest { Id = "1", Method = "buildSolution", ParamsJson = "{}" }, CancellationToken.None);

        serverShouldExit.Release();
        await serverTask;

        Assert.Null(result.Error);
        Assert.Contains("succeeded", result.ResultJson);
    }
}
