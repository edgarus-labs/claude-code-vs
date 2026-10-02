using ClaudeCode.Contracts;
using System;

namespace ClaudeCode.Vsix;

public static class ClaudeCodeServices
{
    /// <summary>
    /// Gets or sets the connection factory.
    /// </summary>
    public static IAcpAgentConnectionFactory? ConnectionFactory { get; set; }
    /// <summary>
    /// Gets or sets the auth service.
    /// </summary>
    public static IAcpAuthService? AuthService { get; set; }
    /// <summary>
    /// Gets or sets the usage service.
    /// </summary>
    public static IUsageService? UsageService { get; set; }
    /// <summary>
    /// Gets or sets the get workspace root.
    /// </summary>
    public static Func<string?>? GetWorkspaceRoot { get; set; }
}
