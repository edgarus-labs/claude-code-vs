using ClaudeCode.Contracts;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

/// <summary>
/// Represents an adapter installer that checks system readiness and asynchronously installs the ACP adapter package.
/// </summary>
public sealed class AcpAdapterInstaller : IAcpAdapterInstaller
{
    /// <summary>
    /// The package name.
    /// </summary>
    private const string _packageName = "@agentclientprotocol/claude-agent-acp";
    private readonly string? _searchPath;
    private readonly Func<string, string[], IProgress<string>?, CancellationToken, Task<int>> _processRunner;

    /// <summary>
    /// Initializes a new instance of the AcpAdapterInstaller class with the specified search path and process runner, or defaults to the system default process runner if none is provided.
    /// </summary>
    /// <param name="searchPath">The search path.</param>
    /// <param name="processRunner">The process runner.</param>
    public AcpAdapterInstaller(
        string? searchPath = null,
        Func<string, string[], IProgress<string>?, CancellationToken, Task<int>>? processRunner = null)
    {
        _searchPath = searchPath;
        _processRunner = processRunner ?? DefaultProcessRunner;
    }

    /// <summary>
    /// Checks the readiness of the adapter by verifying the presence of the configured path, default executable, or Node.js/npm on the system path.
    /// </summary>
    /// <param name="configuredPath">The configured path.</param>
    /// <returns>The adapter readiness result.</returns>
    public AdapterReadiness CheckReadiness(string? configuredPath = null)
    {
        string searchPath = _searchPath ?? AcpExecutableResolver.GetDefaultSearchPath();
        if (!string.IsNullOrWhiteSpace(configuredPath) && AcpExecutableResolver.TryResolve(configuredPath!, searchPath) is not null)
        {
            return AdapterReadiness.Ready;
        }

        if (AcpExecutableResolver.TryResolveDefault(searchPath) is not null)
        {
            return AdapterReadiness.Ready;
        }

        if (AcpExecutableResolver.FindNpmOnPath(searchPath) is not null
            || AcpExecutableResolver.FindNodeOnPath(searchPath) is not null)
        {
            return AdapterReadiness.MissingAdapter;
        }

        return AdapterReadiness.MissingNodeOrNpm;
    }

    /// <summary>
    /// Asynchronously installs the specified npm package globally using either npm or Node.js, reporting progress and handling cancellation.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result is true if successful; otherwise, false.</returns>
    public async Task<bool> InstallAdapterAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string searchPath = _searchPath ?? AcpExecutableResolver.GetDefaultSearchPath();
        string? npm = AcpExecutableResolver.FindNpmOnPath(searchPath);
        string? node = AcpExecutableResolver.FindNodeOnPath(searchPath);

        string fileName;
        string[] arguments;

        if (npm is not null)
        {
            fileName = npm;
            arguments = ["install", "-g", _packageName];
        }
        else if (node is not null)
        {
            string npmCli = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(npmCli))
            {
                fileName = node;
                arguments = [npmCli, "install", "-g", _packageName];
            }
            else
            {
                progress?.Report("npm could not be found to install " + _packageName);

                return false;
            }
        }
        else
        {
            progress?.Report("Neither npm nor Node.js could be found.");

            return false;
        }

        progress?.Report("Running " + Path.GetFileName(fileName) + " install -g " + _packageName + "...");
        int exitCode = await _processRunner(fileName, arguments, progress, cancellationToken).ConfigureAwait(false);
        if (exitCode == 0)
        {
            progress?.Report("Successfully installed " + _packageName);

            return true;
        }

        progress?.Report("Installation of " + _packageName + " failed with exit code " + exitCode);

        return false;
    }

    /// <summary>
    /// Asynchronously executes a process with the specified file name and arguments, capturing and reporting output and error streams while supporting cancellation.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="arguments">The collection of arguments.</param>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the int.</returns>
    private static async Task<int> DefaultProcessRunner(string fileName, string[] arguments, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = ProcessArgumentEscaping.ToArgumentsString(arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };

        string defaultSearchPath = AcpExecutableResolver.GetDefaultSearchPath();
        if (!string.IsNullOrWhiteSpace(defaultSearchPath))
        {
            process.StartInfo.EnvironmentVariables["PATH"] = defaultSearchPath;
        }

        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, __) => exited.TrySetResult(true);

        if (!process.Start())
        {
            return -1;
        }

        async Task ReadStreamAsync(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    progress?.Report(line);
                }
            }
        }

        Task stdout = ReadStreamAsync(process.StandardOutput);
        Task stderr = ReadStreamAsync(process.StandardError);

        try
        {
            await Task.WhenAll(stdout, stderr, ProcessExitWait.WaitForExitAsync(exited.Task, cancellationToken)).ConfigureAwait(false);

            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }

            throw;
        }
    }
}
