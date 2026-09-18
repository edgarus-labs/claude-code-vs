using ClaudeCode.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels.Demo;

public sealed class NullChatSessionServices : IChatSessionServices
{
    public NullChatSessionServices()
    {
        ConnectionFactory = new FakeAcpAgentConnectionFactory();
        AuthService = new FakeAcpAuthService();
    }

    public IAcpAgentConnectionFactory ConnectionFactory { get; }

    public IAcpAuthService AuthService { get; }

    public string? WorkspaceRoot => null;

    public Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<EditorDocumentSnapshot?>(null);

    public Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
