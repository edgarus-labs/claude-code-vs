using System;
using ClaudeCode.Contracts;

namespace ClaudeCode.Vsix
{
    /// <summary>
    /// Process-lifetime service locator populated once by <see cref="ClaudeCodePackage.InitializeAsync"/>.
    /// Deliberately a plain static class with mutable public fields (not a DI container) - this project is
    /// the only place that ever assigns these; every other type in this assembly only reads them. Consumers
    /// outside this assembly (ClaudeCode.Core's ChatPanelView) never reference this type directly - see
    /// <see cref="VsChatSessionServices"/> for the bridge that avoids a circular project reference.
    /// </summary>
    public static class ClaudeCodeServices
    {
        public static IAcpAgentConnectionFactory? ConnectionFactory;
        public static IAcpAuthService? AuthService;
        public static Func<string?>? GetWorkspaceRoot;
    }
}
