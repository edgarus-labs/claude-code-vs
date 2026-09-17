using ClaudeCode.Contracts;

namespace ClaudeCode.Core.ViewModels.Demo
{
    /// <summary>
    /// No-op/demo <see cref="IChatSessionServices"/> used when <c>ChatPanelView.ServicesFactory</c> is null
    /// (XAML designer, unit tests, or a standalone preview) so the control never throws at design time.
    /// </summary>
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
    }
}
