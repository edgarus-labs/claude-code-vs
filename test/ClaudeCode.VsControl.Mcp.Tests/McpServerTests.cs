using ClaudeCode.Contracts;
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.VsControl.Mcp.Tests;

public sealed class McpServerTests
{
    private static readonly JsonSerializerOptions _wireOptions = new(JsonSerializerDefaults.Web);

    private static async Task<JsonObject> RunSingleRequestAsync(VsControlPipeClient pipeClient, string requestLine)
    {
        using var input = new StringReader(requestLine + "\n");
        using var output = new StringWriter { NewLine = "\n" };
        var server = new McpServer(pipeClient, input, output);

        await server.RunAsync(CancellationToken.None);

        string responseLine = output.ToString().TrimEnd('\n');
        Assert.False(string.IsNullOrWhiteSpace(responseLine), "Expected exactly one JSON-RPC response line.");

        return Assert.IsType<JsonObject>(JsonNode.Parse(responseLine));
    }

    [Fact]
    public async Task ToolsList_ReturnsOneToolPerVsControlMethod_WithDescriptionAndSchema()
    {
        await using var pipeClient = new VsControlPipeClient($"unused-{Guid.NewGuid():N}");

        JsonObject response = await RunSingleRequestAsync(pipeClient, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        var tools = Assert.IsType<JsonArray>(response["result"]!["tools"]);
        Assert.Equal(VsControlToolCatalog.Tools.Count, tools.Count);

        var actualNames = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        foreach (var expected in VsControlToolCatalog.Tools)
        {
            Assert.Contains(expected.Name, actualNames);
        }

        foreach (var toolNode in tools)
        {
            var tool = Assert.IsType<JsonObject>(toolNode);
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));

            var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.IsType<JsonObject>(schema["properties"]);
        }
    }

    [Fact]
    public async Task ToolsCall_AgainstRespondingPipeServer_ReturnsSuccessfulToolResult()
    {
        string pipeName = $"vscontrol-test-{Guid.NewGuid():N}";
        using var serverStarted = new SemaphoreSlim(0, 1);

        Task serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            serverStarted.Release();
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            _ = await reader.ReadLineAsync(); // handshake token line, written before any MCP request (F1)

            string? requestLine = await reader.ReadLineAsync();
            Assert.NotNull(requestLine);
            var request = JsonSerializer.Deserialize<VsControlRequest>(requestLine!, _wireOptions);
            Assert.NotNull(request);
            Assert.Equal("openDocument", request!.Method);

            // Canned response echoing the caller's correlation id back, per VsControlProtocol.md.
            var canned = new VsControlResponse { Id = request.Id, ResultJson = "{}" };
            await writer.WriteLineAsync(JsonSerializer.Serialize(canned, _wireOptions));
        });

        await serverStarted.WaitAsync();

        await using var pipeClient = new VsControlPipeClient(pipeName, TimeSpan.FromSeconds(5));

        JsonObject response = await RunSingleRequestAsync(
            pipeClient,
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"openDocument","arguments":{"path":"C:\\foo.cs"}}}""");

        await serverTask;

        var result = Assert.IsType<JsonObject>(response["result"]);
        Assert.False(result["isError"]!.GetValue<bool>());
        var content = Assert.IsType<JsonArray>(result["content"]);
        string text = content[0]!["text"]!.GetValue<string>();
        Assert.StartsWith("<<<UNTRUSTED_TOOL_OUTPUT>>>\n", text);
        Assert.EndsWith("\n<<<END_UNTRUSTED_TOOL_OUTPUT>>>", text);
        Assert.Contains("{}", text);
    }

    [Fact]
    public async Task ToolsCall_TruncatesResultTextLongerThan256Kb_AndAppendsTruncationMarker()
    {
        string pipeName = $"vscontrol-test-truncate-{Guid.NewGuid():N}";
        using var serverStarted = new SemaphoreSlim(0, 1);
        string hugeValue = new string('a', 300_000);

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

            var canned = new VsControlResponse { Id = request!.Id, ResultJson = hugeValue };
            await writer.WriteLineAsync(JsonSerializer.Serialize(canned, _wireOptions));
        });

        await serverStarted.WaitAsync();

        await using var pipeClient = new VsControlPipeClient(pipeName, TimeSpan.FromSeconds(5));

        JsonObject response = await RunSingleRequestAsync(
            pipeClient,
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"getSolutionInfo","arguments":{}}}""");

        await serverTask;

        var result = Assert.IsType<JsonObject>(response["result"]);
        var content = Assert.IsType<JsonArray>(result["content"]);
        string text = content[0]!["text"]!.GetValue<string>();
        Assert.Contains("[truncated: response exceeded 256KB]", text);
        Assert.True(text.Length < hugeValue.Length, "Expected the oversized result to actually be truncated.");
    }

    [Fact]
    public async Task Initialize_WithUnsupportedProtocolVersion_RespondsWithASupportedVersion_NotAnEcho()
    {
        await using var pipeClient = new VsControlPipeClient($"unused-{Guid.NewGuid():N}");

        JsonObject response = await RunSingleRequestAsync(
            pipeClient,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        string protocolVersion = response["result"]!["protocolVersion"]!.GetValue<string>();
        Assert.NotEqual("1999-01-01", protocolVersion);
        Assert.Equal("2024-11-05", protocolVersion);
    }

    [Fact]
    public async Task Initialize_WithASupportedProtocolVersion_EchoesItBack()
    {
        await using var pipeClient = new VsControlPipeClient($"unused-{Guid.NewGuid():N}");

        JsonObject response = await RunSingleRequestAsync(
            pipeClient,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");

        Assert.Equal("2024-11-05", response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolsCall_WithNoPipeServerListening_ReturnsMcpToolErrorNotACrash()
    {
        string pipeName = $"vscontrol-test-nolistener-{Guid.NewGuid():N}";
        await using var pipeClient = new VsControlPipeClient(pipeName, TimeSpan.FromMilliseconds(300));

        JsonObject response = await RunSingleRequestAsync(
            pipeClient,
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"openDocument","arguments":{"path":"C:\\foo.cs"}}}""");

        var result = Assert.IsType<JsonObject>(response["result"]);
        Assert.True(result["isError"]!.GetValue<bool>());
        var content = Assert.IsType<JsonArray>(result["content"]);
        Assert.False(string.IsNullOrWhiteSpace(content[0]!["text"]!.GetValue<string>()));
    }
}
