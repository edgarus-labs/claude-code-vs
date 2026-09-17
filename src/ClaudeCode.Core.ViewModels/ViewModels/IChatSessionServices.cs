using ClaudeCode.Contracts;

namespace ClaudeCode.Core.ViewModels
{
    /// <summary>
    /// Dependencies <see cref="ChatViewModel"/> needs to talk to a real (or fake) ACP agent. The host
    /// (ClaudeCode.Vsix) implements this and injects it via <c>ChatPanelView.ServicesFactory</c> so that
    /// ClaudeCode.Core never has to reference the host assembly or ClaudeCode.Acp directly.
    /// </summary>
    public interface IChatSessionServices
    {
        IAcpAgentConnectionFactory ConnectionFactory { get; }

        IAcpAuthService AuthService { get; }

        /// <summary>Workspace root to pass to <c>session/new</c>. Null lets the caller fall back to CWD.</summary>
        string? WorkspaceRoot { get; }
    }
}
