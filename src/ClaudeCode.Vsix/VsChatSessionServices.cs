using System;
using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;

namespace ClaudeCode.Vsix
{
    /// <summary>
    /// Bridges the host-only <see cref="ClaudeCodeServices"/> locator to the small interface
    /// <see cref="ClaudeCode.Core.Views.ChatPanelView"/> actually depends on, so ClaudeCode.Core never needs
    /// a project reference back to this assembly.
    /// </summary>
    internal sealed class VsChatSessionServices : IChatSessionServices
    {
        private readonly Func<string?> _getWorkspaceRoot;

        public VsChatSessionServices(IAcpAgentConnectionFactory connectionFactory, IAcpAuthService authService, Func<string?> getWorkspaceRoot)
        {
            ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            AuthService = authService ?? throw new ArgumentNullException(nameof(authService));
            _getWorkspaceRoot = getWorkspaceRoot ?? throw new ArgumentNullException(nameof(getWorkspaceRoot));
        }

        public IAcpAgentConnectionFactory ConnectionFactory { get; }

        public IAcpAuthService AuthService { get; }

        public string? WorkspaceRoot => _getWorkspaceRoot();
    }
}
