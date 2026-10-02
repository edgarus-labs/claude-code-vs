using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class AcpAuthCliArgumentsTests
{
    private static readonly AcpExecutableSpec _directExecutable = new("C:\\adapter\\claude-agent-acp.exe", System.Array.Empty<string>());
    private static readonly AcpExecutableSpec _nodeLaunchedExecutable = new("C:\\node\\node.exe", new[] { "C:\\adapter\\dist\\index.js" });

    [Fact]
    public void Status_AppendsCliAuthStatusJson_AfterExecutablesOwnArguments()
    {
        Assert.Equal(new[] { "--cli", "auth", "status", "--json" }, AcpAuthCliArguments.Status(_directExecutable));
        Assert.Equal(new[] { "C:\\adapter\\dist\\index.js", "--cli", "auth", "status", "--json" }, AcpAuthCliArguments.Status(_nodeLaunchedExecutable));
    }

    [Fact]
    public void Login_AppendsCliAuthLoginClaudeai_AfterExecutablesOwnArguments()
    {
        Assert.Equal(new[] { "--cli", "auth", "login", "--claudeai" }, AcpAuthCliArguments.Login(_directExecutable));
        Assert.Equal(new[] { "C:\\adapter\\dist\\index.js", "--cli", "auth", "login", "--claudeai" }, AcpAuthCliArguments.Login(_nodeLaunchedExecutable));
    }

    [Fact]
    public void Logout_AppendsCliAuthLogout_AfterExecutablesOwnArguments()
    {
        Assert.Equal(new[] { "--cli", "auth", "logout" }, AcpAuthCliArguments.Logout(_directExecutable));
        Assert.Equal(new[] { "C:\\adapter\\dist\\index.js", "--cli", "auth", "logout" }, AcpAuthCliArguments.Logout(_nodeLaunchedExecutable));
    }

    [Fact]
    public void NullExecutable_IsRejectedWithArgumentNullException() => Assert.Throws<System.ArgumentNullException>(() => AcpAuthCliArguments.Login(null!));
}
