using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

public sealed class FakeAcpAgentConnection : IAcpAgentConnection
{
    private readonly TimeSpan _chunkDelay;
    private CancellationTokenSource? _turnCts;
    private string _model = "sonnet";

    public FakeAcpAgentConnection(TimeSpan? chunkDelay = null)
    {
        _chunkDelay = chunkDelay ?? TimeSpan.Zero;
    }

    public bool IsInitialized { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsInitialized = true;

        return Task.CompletedTask;
    }

    public Task<NewSessionResult> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken) =>
        Task.FromResult(new NewSessionResult(Guid.NewGuid().ToString("N"), GetConfigOptions()));

    public Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken)
    {
        if (configId != "model" || (value != "sonnet" && value != "opus"))
            throw new ArgumentException("Unknown demo configuration value.", nameof(value));
        _model = value;
        return Task.FromResult(GetConfigOptions());
    }

    private IReadOnlyList<SessionConfigOption> GetConfigOptions() => new[]
    {
        new SessionConfigOption("model", "Model", "model", _model, new[]
        {
            new SessionConfigValue("sonnet", "Sonnet", "Demo model"),
            new SessionConfigValue("opus", "Opus", "Demo model"),
        }),
    };

    public async Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
    {
        var text = string.Concat(content.OfType<ContentBlock.Text>().Select(t => t.Value));
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _turnCts = turnCts;

        try
        {
            var reply = string.IsNullOrWhiteSpace(text) ? "Image received." : $"Echo: {text}";
            foreach (var chunk in SplitIntoChunks(reply))
            {
                turnCts.Token.ThrowIfCancellationRequested();
                SessionUpdate?.Invoke(this, new SessionUpdateEventArgs(sessionId, new SessionUpdate.AgentMessageChunk(chunk)));

                if (_chunkDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_chunkDelay, turnCts.Token).ConfigureAwait(false);
                }
            }

            SessionUpdate?.Invoke(this, new SessionUpdateEventArgs(sessionId, new SessionUpdate.TurnEnded("end_turn")));
        }
        catch (OperationCanceledException)
        {
            SessionUpdate?.Invoke(this, new SessionUpdateEventArgs(sessionId, new SessionUpdate.TurnEnded("cancelled")));
        }
        finally
        {
            _turnCts = null;
        }
    }

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        _turnCts?.Cancel();

        return Task.CompletedTask;
    }

    public event EventHandler<SessionUpdateEventArgs>? SessionUpdate;

    public event EventHandler<PermissionRequestEventArgs>? PermissionRequested;

    public event EventHandler<FileReadRequestEventArgs>? FileReadRequested;

    public event EventHandler<FileWriteRequestEventArgs>? FileWriteRequested;

    public event EventHandler<Exception?>? Disconnected;

    public ValueTask DisposeAsync() => default;

    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            yield return i == 0 ? words[i] : " " + words[i];
        }
    }
}
