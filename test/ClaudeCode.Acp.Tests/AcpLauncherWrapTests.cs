using System;
using System.IO;
using ClaudeCode.Acp;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class AcpLauncherWrapTests : IDisposable
{
    // A space in the path is the part that matters here; Wrap performs no shell quoting or
    // environment expansion, so the name no longer implies coverage of either.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude acp launcher " + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunsTheLauncherAgainstThePackageRoot_ForTheAdapterDistEntry(bool forwardSlashes)
    {
        if (forwardSlashes && Path.DirectorySeparatorChar != '\\')
        {
            // The alternate flavour only differs from the native one on Windows.
            return;
        }

        string packageDirectory = Path.Combine(_root, "node_modules", "@agentclientprotocol", "claude-agent-acp");
        string entry = CreateFile(Path.Combine(packageDirectory, "dist", "index.js"));
        CreateFile(Path.Combine(packageDirectory, "dist", "acp-agent.js"));
        string launcher = CreateFile(Path.Combine(_root, "Resources", "Scripts", "claude-acp-vs.mjs"));
        var resolved = new AcpExecutableSpec("node.exe", new[] { forwardSlashes ? entry.Replace('\\', '/') : entry });

        var (fileName, arguments, environment) = AcpLauncherWrap.Wrap(resolved, launcher);

        Assert.Equal("node.exe", fileName);
        Assert.Equal(new[] { launcher }, arguments);
        Assert.NotNull(environment);
        Assert.Equal(packageDirectory, Path.GetFullPath(environment!["CLAUDE_ACP_ADAPTER_DIR"]));
    }

    [Fact]
    public void RunsTheAdapterUnchanged_ForANativeExecutableWithNoArguments()
    {
        string launcher = CreateFile(Path.Combine(_root, "Resources", "Scripts", "claude-acp-vs.mjs"));
        var resolved = new AcpExecutableSpec(Path.Combine(_root, "claude-agent-acp.exe"), Array.Empty<string>());

        var (fileName, arguments, environment) = AcpLauncherWrap.Wrap(resolved, launcher);

        Assert.Equal(resolved.FileName, fileName);
        Assert.Empty(arguments!);
        Assert.Null(environment);
    }

    [Fact]
    public void RunsTheAdapterUnchanged_WhenTheEntryIsNotTheSoleArgument()
    {
        string packageDirectory = Path.Combine(_root, "node_modules", "@agentclientprotocol", "claude-agent-acp");
        string entry = CreateFile(Path.Combine(packageDirectory, "dist", "index.js"));
        CreateFile(Path.Combine(packageDirectory, "dist", "acp-agent.js"));
        string launcher = CreateFile(Path.Combine(_root, "Resources", "Scripts", "claude-acp-vs.mjs"));
        var resolved = new AcpExecutableSpec("node.exe", new[] { "--enable-source-maps", entry });

        var (fileName, arguments, environment) = AcpLauncherWrap.Wrap(resolved, launcher);

        Assert.Equal("node.exe", fileName);
        Assert.Equal(new[] { "--enable-source-maps", entry }, arguments);
        Assert.Null(environment);
    }

    [Fact]
    public void RunsTheAdapterUnchanged_WhenTheLauncherIsNotDeployed()
    {
        string packageDirectory = Path.Combine(_root, "node_modules", "@agentclientprotocol", "claude-agent-acp");
        string entry = CreateFile(Path.Combine(packageDirectory, "dist", "index.js"));
        CreateFile(Path.Combine(packageDirectory, "dist", "acp-agent.js"));
        var resolved = new AcpExecutableSpec("node.exe", new[] { entry });

        var (fileName, arguments, environment) = AcpLauncherWrap.Wrap(resolved, Path.Combine(_root, "Resources", "Scripts", "claude-acp-vs.mjs"));

        Assert.Equal("node.exe", fileName);
        Assert.Equal(new[] { entry }, arguments);
        Assert.Null(environment);
    }

    [Fact]
    public void RunsTheAdapterUnchanged_WhenTheAdapterNoLongerShipsTheInternalEntryTheLauncherImports()
    {
        // The launcher imports <pkg>/dist/acp-agent.js, which the adapter does not publish as an
        // entry point; a user-updated adapter that renamed it must still start, without extensions.
        string packageDirectory = Path.Combine(_root, "node_modules", "@agentclientprotocol", "claude-agent-acp");
        string entry = CreateFile(Path.Combine(packageDirectory, "dist", "index.js"));
        string launcher = CreateFile(Path.Combine(_root, "Resources", "Scripts", "claude-acp-vs.mjs"));
        var resolved = new AcpExecutableSpec("node.exe", new[] { entry });

        var (fileName, arguments, environment) = AcpLauncherWrap.Wrap(resolved, launcher);

        Assert.Equal("node.exe", fileName);
        Assert.Equal(new[] { entry }, arguments);
        Assert.Null(environment);
    }

    private string CreateFile(string relativeOrAbsolutePath)
    {
        string path = Path.IsPathRooted(relativeOrAbsolutePath) ? relativeOrAbsolutePath : Path.Combine(_root, relativeOrAbsolutePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
