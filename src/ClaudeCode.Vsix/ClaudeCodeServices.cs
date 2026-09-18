using ClaudeCode.Contracts;
using System;

namespace ClaudeCode.Vsix;

public static class ClaudeCodeServices
{
    public static IAcpAgentConnectionFactory? ConnectionFactory { get; set; }
    public static IAcpAuthService? AuthService { get; set; }
    public static Func<string?>? GetWorkspaceRoot { get; set; }
}
