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
    public void WindowsNpmShim_BinFallback_NeverPrefersAdjacentNodeEvenWithoutNodeModulesSegment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Attack: a shim directory literally named ".bin" without "node_modules" anywhere in its
        // path still reaches the ".bin" fallback (ContainsNodeModulesSegment finds no literal
        // "node_modules" segment and would otherwise treat the directory as trusted) - a planted
        // node.exe sitting next to the shim must never be trusted just because that literal path
        // segment happens to be absent.
        string shim = CreateFile(Path.Combine("tools", ".bin", "claude-agent-acp.cmd"));
        string script = CreateFile(Path.Combine("tools", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"));
        string plantedNode = CreateFile(Path.Combine("tools", ".bin", "node.exe"));
        string pathNode = CreateFile(Path.Combine("real-node", "node.exe"));

        var resolved = AcpExecutableResolver.TryResolve(shim, Path.GetDirectoryName(pathNode));

        Assert.NotNull(resolved);
        Assert.Equal(pathNode, resolved.FileName);
        Assert.NotEqual(plantedNode, resolved.FileName);
        Assert.Equal(new[] { script }, resolved.Arguments);
    }

    [Fact]
    public void ExplicitJsEntryPoint_PrefersPathNodeOverAdjacentPackageNode()
    {
        // Unlike an npm-global .cmd shim (which legitimately colocates its own node.exe), an explicit
        // .js entry point's directory is untrusted package content (node_modules); a "node.exe" sitting
        // next to it must never be preferred over a PATH-resolved node.
        string nodeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        string script = CreateFile(Path.Combine("node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"));
        CreateFile(Path.Combine("node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", nodeName));
        string pathNode = CreateFile(Path.Combine("other", nodeName));

        var resolved = AcpExecutableResolver.TryResolve(script, Path.GetDirectoryName(pathNode));

        Assert.NotNull(resolved);
        Assert.Equal(pathNode, resolved.FileName);
        Assert.Equal(new[] { script }, resolved.Arguments);
    }

    [Fact]
    public void GetSearchDirectories_IgnoresRelativePathEntries_ToAvoidUntrustedWorkingDirectoryResolution()
    {
        string originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.CreateDirectory(_root);
            Directory.SetCurrentDirectory(_root);
            string adapterName = OperatingSystem.IsWindows() ? "claude-agent-acp.exe" : "claude-agent-acp";
            CreateFile(Path.Combine("relative-dir", adapterName));

            // A relative PATH entry must never be resolved against the current working directory - a
            // malicious repository could otherwise plant an adapter executable that gets launched just
            // because the process's CWD happens to be inside (or under) the opened workspace.
            Assert.Null(AcpExecutableResolver.TryResolveDefault("relative-dir"));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
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
