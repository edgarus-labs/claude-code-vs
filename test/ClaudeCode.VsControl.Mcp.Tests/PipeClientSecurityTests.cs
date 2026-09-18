using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.VsControl.Mcp.Tests;

[SupportedOSPlatform("windows")]
public sealed class PipeClientSecurityTests
{
    [Fact]
    public async Task SendAsync_ServerCanIdentifyButCannotImpersonateClient()
    {
        string pipeName = $"vscontrol-sqos-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        TokenImpersonationLevel? observed = null;
        Task serve = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync();
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            _ = await reader.ReadLineAsync();
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
                observed = identity?.ImpersonationLevel;
            });
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var request = JsonSerializer.Deserialize<VsControlRequest>((await reader.ReadLineAsync())!, options)!;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new VsControlResponse { Id = request.Id, ResultJson = "{}" }, options));
        });
        await using var client = new VsControlPipeClient(pipeName, handshakeToken: "token");
        await client.SendAsync(new VsControlRequest { Id = "security", Method = "getSolutionInfo" }, CancellationToken.None);
        await serve;
        Assert.Equal(TokenImpersonationLevel.Identification, observed);
    }
}
