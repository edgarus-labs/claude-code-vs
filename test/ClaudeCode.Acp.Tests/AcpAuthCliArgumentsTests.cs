using ClaudeCode.Acp;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class AcpAuthCliArgumentsTests
{
    private static readonly AcpExecutableSpec DirectExecutable = new("C:\\adapter\\claude-agent-acp.exe", System.Array.Empty<string>());
    private static readonly AcpExecutableSpec NodeLaunchedExecutable = new("C:\\node\\node.exe", new[] { "C:\\adapter\\dist\\index.js" });

    [Fact]
    public void Status_AppendsCliAuthStatusJson_AfterExecutablesOwnArguments()
    {
        Assert.Equal(new[] { "--cli", "auth", "status", "--json" }, AcpAuthCliArguments.Status(DirectExecutable));
        Assert.Equal(new[] { "C:\\adapter\\dist\\index.js", "--cli", "auth", "status", "--json" }, AcpAuthCliArguments.Status(NodeLaunchedExecutable));
    }

    [Fact]
    public void Login_AppendsCliAuthLoginClaudeai_AfterExecutablesOwnArguments()
    {
        Assert.Equal(new[] { "--cli", "auth", "login", "--claudeai" }, AcpAuthCliArguments.Login(DirectExecutable));
        Assert.Equal(new[] { "C:\\adapter\\dist\\index.js", "--cli", "auth", "login", "--claudeai" }, AcpAuthCliArguments.Login(NodeLaunchedExecutable));
    }

    [Fact]
    public void Logout_AppendsCliAuthLogout_AfterExecutablesOwnArguments()
    {
        Assert.Equal(new[] { "--cli", "auth", "logout" }, AcpAuthCliArguments.Logout(DirectExecutable));
        Assert.Equal(new[] { "C:\\adapter\\dist\\index.js", "--cli", "auth", "logout" }, AcpAuthCliArguments.Logout(NodeLaunchedExecutable));
    }
}
