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

    /// <summary>
    /// Attempts to read the live, unsaved contents of an editor buffer currently open in the host for
    /// <paramref name="path"/>, bypassing disk. Returns null when no such buffer is open — caller falls
    /// back to reading the file from disk.
    /// </summary>
    Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to write <paramref name="text"/> into an editor buffer currently open in the host for
    /// <paramref name="path"/>, bypassing a raw disk write. Returns false when no such buffer is open —
    /// caller falls back to writing the file to disk. A rejected edit must throw, never return false,
    /// so a host permission or read-only restriction cannot become a disk-write fallback.
    /// </summary>
    Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken);
}
