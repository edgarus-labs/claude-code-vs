using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;

namespace ClaudeCode.Core.ViewModels.Demo
{
    /// <summary>
    /// In-memory fake used when no real host services are wired up (design time, unit tests, or standalone
    /// preview). Echoes the user's prompt back word-by-word as a streamed assistant reply.
    /// </summary>
    public sealed class FakeAcpAgentConnection : IAcpAgentConnection
    {
        private readonly TimeSpan _chunkDelay;
        private CancellationTokenSource? _turnCts;

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

        public Task<string> NewSessionAsync(string cwd, IReadOnlyList<McpServerConfig>? mcpServers, CancellationToken cancellationToken)
        {
            return Task.FromResult(Guid.NewGuid().ToString("N"));
        }

        public async Task SendPromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken)
        {
            var text = string.Concat(content.OfType<ContentBlock.Text>().Select(t => t.Value));
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _turnCts = turnCts;

            try
            {
                var reply = string.IsNullOrWhiteSpace(text) ? "(empty message)" : $"Echo: {text}";
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
}
