using ClaudeCode.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.ViewModels;

public interface IChatSessionServices
{
    IAcpAgentConnectionFactory ConnectionFactory { get; }

    IAcpAuthService AuthService { get; }

    IUsageService UsageService { get; }

    string? WorkspaceRoot { get; }

    /// <summary>True while the host has a text document that <see cref="CaptureActiveDocumentAsync"/>
    /// can capture.</summary>
    bool HasActiveDocument { get; }

    /// <summary>Raised when <see cref="HasActiveDocument"/> may have changed. May fire on any thread.</summary>
    event EventHandler? ActiveDocumentChanged;

    /// <summary>Raised when <see cref="WorkspaceRoot"/> may have changed, i.e. the host opened or
    /// closed a solution/workspace. May fire on any thread, including when the root is unchanged.</summary>
    event EventHandler? WorkspaceRootChanged;

    /// <summary>Whether every new session should have Remote Control (claude.ai/code) turned on automatically.</summary>
    bool RemoteControlAtStartup { get; }

    Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens (or activates) <paramref name="path"/> in the host editor and, when
    /// <paramref name="line"/> is supplied, moves the caret to that 1-based line. The returned task
    /// faults when the host cannot open the file. A line the document does not have is clamped.
    /// </summary>
    Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the live, unsaved contents of an editor buffer open in the host for
    /// <paramref name="path"/>. Returns null when no such buffer is open.
    /// </summary>
    Task<string?> TryReadOpenDocumentAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="text"/> into an editor buffer open in the host for
    /// <paramref name="path"/>. Returns false when no such buffer is open; throws when the edit is rejected.
    /// </summary>
    Task<bool> TryWriteOpenDocumentAsync(string path, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the user to confirm signing out of Claude Code everywhere on this machine (CLI, VS Code,
    /// other clients), not only Visual Studio. Returns false if the user declines or cancels.
    /// </summary>
    Task<bool> ConfirmSignOutEverywhereAsync(CancellationToken cancellationToken);
}
