using ClaudeCode.Contracts;
using System;

namespace ClaudeCode.Vsix;

/// <summary>
/// Provides static factory and service accessors for configuring the Claude Code ACP agent, including its connection, authentication, usage tracking, adapter installation, and workspace root resolution.
/// </summary>
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
    /// Gets or sets the adapter installer.
    /// </summary>
    public static IAcpAdapterInstaller? AdapterInstaller { get; set; }

    /// <summary>
    /// Gets or sets the get workspace root.
    /// </summary>
    public static Func<string?>? GetWorkspaceRoot { get; set; }
}
