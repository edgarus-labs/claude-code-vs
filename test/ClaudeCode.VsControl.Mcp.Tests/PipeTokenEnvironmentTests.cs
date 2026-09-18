using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.VsControl.Mcp.Tests;

[CollectionDefinition("Pipe token environment", DisableParallelization = true)]
public sealed class PipeTokenEnvironmentScope
{
}

[Collection("Pipe token environment")]
public sealed class PipeTokenEnvironmentTests
{
    private const string TokenVariable = "CLAUDECODE_VSCONTROL_TOKEN";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("token\nrequest")]
    [InlineData("token\rrequest")]
    public void Constructor_RejectsMissingOrMalformedEnvironmentToken(string? token)
    {
        string? previous = Environment.GetEnvironmentVariable(TokenVariable);
        try
        {
            Environment.SetEnvironmentVariable(TokenVariable, token);
            Assert.Throws<ArgumentException>(() => new VsControlPipeClient("unused"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previous);
        }
    }

    [Fact]
    public async Task SendAsync_UsesEnvironmentTokenForHandshake()
    {
        string? previous = Environment.GetEnvironmentVariable(TokenVariable);
        try
        {
            Environment.SetEnvironmentVariable(TokenVariable, "environment-token");
            string pipeName = $"vscontrol-env-{Guid.NewGuid():N}";
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Task serve = Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                Assert.Equal("environment-token", await reader.ReadLineAsync());
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                var request = JsonSerializer.Deserialize<VsControlRequest>((await reader.ReadLineAsync())!, options)!;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new VsControlResponse { Id = request.Id, ResultJson = "{}" }, options));
            });
            await using var client = new VsControlPipeClient(pipeName);
            var response = await client.SendAsync(new VsControlRequest { Id = "env", Method = "getSolutionInfo" }, CancellationToken.None);
            await serve;
            Assert.Null(response.Error);
            Assert.Equal("{}", response.ResultJson);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previous);
        }
    }
}
