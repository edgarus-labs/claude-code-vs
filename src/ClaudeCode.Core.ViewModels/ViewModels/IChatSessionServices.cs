using ClaudeCode.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels;

public interface IChatSessionServices
{
    IAcpAgentConnectionFactory ConnectionFactory { get; }

    IAcpAuthService AuthService { get; }

    string? WorkspaceRoot { get; }

    Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken);
}
