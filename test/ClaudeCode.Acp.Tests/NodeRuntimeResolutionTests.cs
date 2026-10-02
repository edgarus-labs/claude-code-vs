using System;
using System.IO;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>Covers <see cref="AcpExecutableResolver.FindNodeOnPath(string?)"/>, the PATH search for a
/// Node runtime used by the ACP adapter launch and the VSIX usage helper.</summary>
[Collection("Process current directory")]
public sealed class NodeRuntimeResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude node & %path% " + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindNodeOnPath_ReturnsFullyQualifiedRuntimeFromSearchPath()
    {
        string node = CreateFile(OperatingSystem.IsWindows() ? "node.exe" : "node");

        Assert.Equal(node, AcpExecutableResolver.FindNodeOnPath("\"" + _root + "\""));
    }

    [Fact]
    public void FindNodeOnPath_SkipsPackageDirectoriesSoAWorkspaceCannotPlantTheRuntime()
    {
        string planted = Path.GetDirectoryName(CreateFile(Path.Combine("node_modules", ".bin", OperatingSystem.IsWindows() ? "node.exe" : "node")))!;
        string trusted = CreateFile(Path.Combine("program files", "nodejs", OperatingSystem.IsWindows() ? "node.exe" : "node"));

        string? resolved = AcpExecutableResolver.FindNodeOnPath(
            planted + Path.PathSeparator + Path.GetDirectoryName(trusted));

        Assert.Equal(trusted, resolved);
    }

    [Fact]
    public void FindNodeOnPath_IgnoresRelativeSearchPathEntries()
    {
        string originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.CreateDirectory(_root);
            Directory.SetCurrentDirectory(_root);
            CreateFile(Path.Combine("relative-dir", OperatingSystem.IsWindows() ? "node.exe" : "node"));

            Assert.Null(AcpExecutableResolver.FindNodeOnPath("relative-dir"));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
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
