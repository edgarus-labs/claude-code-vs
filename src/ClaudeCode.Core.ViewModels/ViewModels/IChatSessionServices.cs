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
    /// could capture; drives the enabled state of the attach-document command.</summary>
    bool HasActiveDocument { get; }

    /// <summary>Raised when <see cref="HasActiveDocument"/> may have changed. May fire on any thread.</summary>
    event EventHandler? ActiveDocumentChanged;

    /// <summary>Raised when <see cref="WorkspaceRoot"/> may have changed, i.e. the host opened or
    /// closed a solution/workspace. May fire on any thread, and may fire when the root is in fact
    /// unchanged (a solution reload reopens the same root); the handler decides what changed.</summary>
    event EventHandler? WorkspaceRootChanged;

    /// <summary>Whether every new session should have Remote Control (claude.ai/code) turned on automatically.</summary>
    bool RemoteControlAtStartup { get; }

    Task<EditorDocumentSnapshot?> CaptureActiveDocumentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens (or activates) <paramref name="path"/> in the host editor and, when
    /// <paramref name="line"/> is supplied, moves the caret to that 1-based line. Faults when the
    /// host cannot open it (deleted, locked, or not a document the host can display); the returned
    /// task carries that failure, so a caller that surfaces the command to the user must observe it
    /// rather than let an <c>AsyncRelayCommand</c> rethrow it onto the UI thread. A line the
    /// document does not have is clamped, never an error: the file is still what the user asked for.
    /// </summary>
    Task OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken);

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

    /// <summary>
    /// Asks the user to confirm signing out of Claude Code everywhere on this machine (CLI, VS Code,
    /// other clients), not only Visual Studio. Returns false if the user declines or cancels.
    /// </summary>
    Task<bool> ConfirmSignOutEverywhereAsync(CancellationToken cancellationToken);
}
