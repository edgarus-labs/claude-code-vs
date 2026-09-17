using System;
using System.IO;
using ClaudeCode.Acp;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class AcpExecutableResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude acp & %resolver% " + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultResolution_DoesNotFallBackToLegacyAdapterOrNativeCli()
    {
        CreateFile("claude-code-acp");
        CreateFile("claude-code-acp.cmd");
        CreateFile("claude-code-acp.exe");
        CreateFile("claude");
        CreateFile("claude.cmd");
        string nativeCli = CreateFile("claude.exe");

        Assert.Null(AcpExecutableResolver.TryResolveDefault(_root));
        Assert.Null(AcpExecutableResolver.TryResolve(nativeCli, _root));
    }

    [Fact]
    public void DefaultResolution_FindsCurrentExecutableOnQuotedPath()
    {
        string executable = CreateFile(Path.Combine("adapter", OperatingSystem.IsWindows() ? "claude-agent-acp.exe" : "claude-agent-acp"));
        string searchPath = Path.Combine(_root, "missing") + Path.PathSeparator + '"' + Path.GetDirectoryName(executable) + '"';

        var resolved = AcpExecutableResolver.TryResolveDefault(searchPath);

        Assert.NotNull(resolved);
        Assert.Equal(executable, resolved.FileName);
        Assert.Empty(resolved.Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsNpmShim_ResolvesNodeAndPackageScriptWithoutShell(bool localInstall)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string shimDirectory = localInstall ? Path.Combine("node_modules", ".bin") : "npm";
        string packageDirectory = localInstall ? "node_modules" : Path.Combine("npm", "node_modules");
        string shim = CreateFile(Path.Combine(shimDirectory, "claude-agent-acp.cmd"));
        string script = CreateFile(Path.Combine(packageDirectory, "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"));
        string node = CreateFile(Path.Combine("node runtime", "node.exe"));
        string searchPath = Path.GetDirectoryName(shim) + Path.PathSeparator.ToString() + Path.GetDirectoryName(node);

        var resolved = AcpExecutableResolver.TryResolveDefault(searchPath);

        Assert.NotNull(resolved);
        Assert.Equal(node, resolved.FileName);
        Assert.Equal(new[] { script }, resolved.Arguments);
    }

    [Fact]
    public void WindowsNpmShim_PrefersAdjacentNodeOverPathNode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string shim = CreateFile(Path.Combine("npm", "claude-agent-acp.cmd"));
        string script = CreateFile(Path.Combine("npm", "node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"));
        string adjacentNode = CreateFile(Path.Combine("npm", "node.exe"));
        string pathNode = CreateFile(Path.Combine("other", "node.exe"));

        var resolved = AcpExecutableResolver.TryResolve(shim, Path.GetDirectoryName(pathNode));

        Assert.NotNull(resolved);
        Assert.Equal(adjacentNode, resolved.FileName);
        Assert.Equal(new[] { script }, resolved.Arguments);
    }

    [Fact]
    public void WindowsNpmShim_RequiresPackageAndNodeInsteadOfReturningCmd()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string shim = CreateFile("claude-agent-acp.cmd");
        Assert.Null(AcpExecutableResolver.TryResolve(shim, _root));

        CreateFile(Path.Combine("node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"));
        Assert.Null(AcpExecutableResolver.TryResolve(shim, _root));

        string nativeAdapter = CreateFile(Path.Combine("later", "claude-agent-acp.exe"));
        var resolved = AcpExecutableResolver.TryResolveDefault(_root + Path.PathSeparator.ToString() + Path.GetDirectoryName(nativeAdapter));
        Assert.NotNull(resolved);
        Assert.Equal(nativeAdapter, resolved.FileName);
    }

    [Fact]
    public void ExplicitPackageEntryPoint_UsesNodeWithoutNeedingNpmShim()
    {
        string script = CreateFile(Path.Combine("node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"));
        string node = CreateFile(OperatingSystem.IsWindows() ? "node.exe" : "node");

        var resolved = AcpExecutableResolver.TryResolve(script, _root);

        Assert.NotNull(resolved);
        Assert.Equal(node, resolved.FileName);
        Assert.Equal(new[] { script }, resolved.Arguments);
    }

    private string CreateFile(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
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
