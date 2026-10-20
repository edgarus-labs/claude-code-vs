using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

[Collection("Process current directory")]
public sealed class AcpAdapterInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude acp & %installer% " + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindNpmOnPath_WhenNpmPresent_ReturnsPath()
    {
        string npmName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        string npm = CreateFile(Path.Combine("bin", npmName));

        string? found = AcpExecutableResolver.FindNpmOnPath(Path.GetDirectoryName(npm));

        Assert.Equal(npm, found);
    }

    [Fact]
    public void FindNpmOnPath_WhenInPackageDirectory_RejectsUntrusted()
    {
        string npmName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        string plantedNpm = CreateFile(Path.Combine("node_modules", ".bin", npmName));

        string? found = AcpExecutableResolver.FindNpmOnPath(Path.GetDirectoryName(plantedNpm));

        Assert.Null(found);
    }

    [Fact]
    public void CheckReadiness_WhenAdapterResolvable_ReturnsReady()
    {
        string adapterName = OperatingSystem.IsWindows() ? "claude-agent-acp.exe" : "claude-agent-acp";
        string adapter = CreateFile(Path.Combine("bin", adapterName));
        string searchPath = Path.GetDirectoryName(adapter)!;

        var installer = new AcpAdapterInstaller(searchPath);

        Assert.Equal(AdapterReadiness.Ready, installer.CheckReadiness());
    }

    [Fact]
    public void CheckReadiness_WhenAdapterMissing_AndNpmFound_ReturnsMissingAdapter()
    {
        string npmName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        string npm = CreateFile(Path.Combine("bin", npmName));
        string searchPath = Path.GetDirectoryName(npm)!;

        var installer = new AcpAdapterInstaller(searchPath);

        Assert.Equal(AdapterReadiness.MissingAdapter, installer.CheckReadiness());
    }

    [Fact]
    public void CheckReadiness_WhenAdapterAndNpmMissing_ReturnsMissingNodeOrNpm()
    {
        string emptyDir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyDir);

        var installer = new AcpAdapterInstaller(emptyDir);

        Assert.Equal(AdapterReadiness.MissingNodeOrNpm, installer.CheckReadiness());
    }

    [Fact]
    public async Task InstallAdapterAsync_InvokesRunnerWithNpmAndArguments_ReturnsTrueOnSuccess()
    {
        string npmName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        string npm = CreateFile(Path.Combine("bin", npmName));
        string searchPath = Path.GetDirectoryName(npm)!;

        string? executedFile = null;
        string[]? executedArgs = null;
        var reported = new List<string>();

        var installer = new AcpAdapterInstaller(searchPath, (file, args, progress, ct) =>
        {
            executedFile = file;
            executedArgs = args;
            progress?.Report("npm http fetch 200");
            progress?.Report("added 1 package");

            return Task.FromResult(0);
        });

        var progressMock = new Progress<string>(reported.Add);
        bool result = await installer.InstallAdapterAsync(progressMock, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(npm, executedFile);
        Assert.Equal(new[] { "install", "-g", "@agentclientprotocol/claude-agent-acp" }, executedArgs);
        Assert.Contains("added 1 package", reported);
    }

    [Fact]
    public async Task InstallAdapterAsync_WhenRunnerFails_ReturnsFalse()
    {
        string npmName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        string npm = CreateFile(Path.Combine("bin", npmName));
        string searchPath = Path.GetDirectoryName(npm)!;

        var installer = new AcpAdapterInstaller(searchPath, (file, args, progress, ct) => Task.FromResult(1));

        bool result = await installer.InstallAdapterAsync(null, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task DefaultSearchPath_UsesSystemSearchPathForReadinessAndInstall()
    {
        string adapterName = OperatingSystem.IsWindows() ? "claude-agent-acp.exe" : "claude-agent-acp";
        string npmName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        string adapter = CreateFile(Path.Combine("default-bin", adapterName));
        string npm = CreateFile(Path.Combine("default-bin", npmName));
        string binDir = Path.GetDirectoryName(adapter)!;
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", binDir + Path.PathSeparator + originalPath);
            string? executedFile = null;
            var installer = new AcpAdapterInstaller(
                searchPath: null,
                processRunner: (file, args, progress, ct) =>
                {
                    executedFile = file;
                    return Task.FromResult(0);
                });

            Assert.Equal(AdapterReadiness.Ready, installer.CheckReadiness());
            Assert.True(await installer.InstallAdapterAsync(null, CancellationToken.None));
            Assert.Equal(npm, executedFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
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
