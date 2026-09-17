using ClaudeCode.Contracts;
using System;

namespace ClaudeCode.Vsix;

public static class ClaudeCodeServices
{
    public static IAcpAgentConnectionFactory? ConnectionFactory;
    public static IAcpAuthService? AuthService;
    public static Func<string?>? GetWorkspaceRoot;
}
