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

    [Theory]
    [InlineData("v22.23.3", true)]
    [InlineData("v20.19.5", true)]
    [InlineData("v22.23.4", false)]
    [InlineData("v24.22.1", false)]
    [InlineData("v27.0.0", false)]
    public async Task GetRuntimeWarningAsync_ProbesResolvedNodeAndAppliesPolicy(string reportedVersion, bool expectWarning)
    {
        var (script, node) = CreateNodeAdapter();
        string? executedFile = null;
        string[]? executedArgs = null;

        var installer = new AcpAdapterInstaller(Path.GetDirectoryName(node)!, (file, args, progress, ct) =>
        {
            executedFile = file;
            executedArgs = args;
            progress?.Report(reportedVersion);

            return Task.FromResult(0);
        });

        string? warning = await installer.GetRuntimeWarningAsync(script, CancellationToken.None);

        Assert.Equal(node, executedFile);
        Assert.Equal(new[] { "--version" }, executedArgs);
        Assert.Equal(expectWarning, warning is not null);
        if (expectWarning)
        {
            Assert.Contains(reportedVersion.TrimStart('v'), warning);
        }
    }

    [Fact]
    public async Task GetRuntimeWarningAsync_WhenProbeFails_DoesNotWarn()
    {
        var (script, node) = CreateNodeAdapter();
        var installer = new AcpAdapterInstaller(Path.GetDirectoryName(node)!, (file, args, progress, ct) =>
        {
            progress?.Report("v20.19.5");

            return Task.FromResult(1);
        });

        Assert.Null(await installer.GetRuntimeWarningAsync(script, CancellationToken.None));
    }

    [Fact]
    public async Task GetRuntimeWarningAsync_WhenProbeThrows_DoesNotWarn()
    {
        var (script, node) = CreateNodeAdapter();
        var installer = new AcpAdapterInstaller(
            Path.GetDirectoryName(node)!,
            (file, args, progress, ct) => throw new InvalidOperationException("cannot start"));

        Assert.Null(await installer.GetRuntimeWarningAsync(script, CancellationToken.None));
    }

    [Fact]
    public async Task GetRuntimeWarningAsync_WhenProbeOutputIsNotAVersion_DoesNotWarn()
    {
        var (script, node) = CreateNodeAdapter();
        var installer = new AcpAdapterInstaller(Path.GetDirectoryName(node)!, (file, args, progress, ct) =>
        {
            progress?.Report("something unexpected");

            return Task.FromResult(0);
        });

        Assert.Null(await installer.GetRuntimeWarningAsync(script, CancellationToken.None));
    }

    [Fact]
    public async Task GetRuntimeWarningAsync_WhenCallerCancels_Throws()
    {
        var (script, node) = CreateNodeAdapter();
        using var cts = new CancellationTokenSource();
        var installer = new AcpAdapterInstaller(Path.GetDirectoryName(node)!, (file, args, progress, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();

            return Task.FromResult(0);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.GetRuntimeWarningAsync(script, cts.Token));
    }

    [Fact]
    public async Task GetRuntimeWarningAsync_WhenAdapterIsNativeExecutable_DoesNotProbeNode()
    {
        string adapterName = OperatingSystem.IsWindows() ? "claude-agent-acp.exe" : "claude-agent-acp";
        string adapter = CreateFile(Path.Combine("native", adapterName));
        string node = CreateFile(Path.Combine("native", OperatingSystem.IsWindows() ? "node.exe" : "node"));
        bool probed = false;
        var installer = new AcpAdapterInstaller(Path.GetDirectoryName(node)!, (file, args, progress, ct) =>
        {
            probed = true;

            return Task.FromResult(0);
        });

        Assert.Null(await installer.GetRuntimeWarningAsync(adapter, CancellationToken.None));
        Assert.False(probed);
    }

    [Fact]
    public async Task GetRuntimeWarningAsync_WhenAdapterNotResolvable_DoesNotProbeNode()
    {
        string node = CreateFile(Path.Combine("bare", OperatingSystem.IsWindows() ? "node.exe" : "node"));
        bool probed = false;
        var installer = new AcpAdapterInstaller(Path.GetDirectoryName(node)!, (file, args, progress, ct) =>
        {
            probed = true;

            return Task.FromResult(0);
        });

        Assert.Null(await installer.GetRuntimeWarningAsync(null, CancellationToken.None));
        Assert.False(probed);
    }

    private (string Script, string Node) CreateNodeAdapter()
    {
        string script = CreateFile(Path.Combine("lib", "node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js"));
        string node = CreateFile(Path.Combine("nodebin", OperatingSystem.IsWindows() ? "node.exe" : "node"));

        return (script, node);
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
