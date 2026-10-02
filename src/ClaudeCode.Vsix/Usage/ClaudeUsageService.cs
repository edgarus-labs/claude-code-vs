using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Usage;

internal sealed class ClaudeUsageService : IUsageService
{
    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(10);
    /// <summary>
    /// The max output characters.
    /// </summary>
    private const int _maxOutputCharacters = 64 * 1024;

    private readonly string _scriptPath;
    private readonly object _cacheLock = new object();
    private UsageSnapshot? _cachedSnapshot;
    private JoinableTask<UsageSnapshot?>? _inFlight;

    /// <summary>
    /// Initializes a new instance of the ClaudeUsageService class with the specified script path, throwing an ArgumentNullException if scriptPath is null.
    /// </summary>
    /// <param name="scriptPath">The script path.</param>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
    public ClaudeUsageService(string scriptPath)
    {
        _scriptPath = scriptPath ?? throw new ArgumentNullException(nameof(scriptPath));
    }

    public async Task<UsageSnapshot?> GetUsageAsync(CancellationToken cancellationToken)
    {
        JoinableTask<UsageSnapshot?> fetch;
        lock (_cacheLock)
        {
            if (_cachedSnapshot is not null && UsageServiceRules.IsWithinBurstWindow(_cachedSnapshot.FetchedAt, DateTimeOffset.UtcNow))
            {
                return _cachedSnapshot;
            }

            if (_inFlight is null || _inFlight.IsCompleted)
            {
#pragma warning disable VSSDK007
                _inFlight = ThreadHelper.JoinableTaskFactory.RunAsync(FetchAndCacheAsync);
#pragma warning restore VSSDK007
            }

            fetch = _inFlight;
        }

        return await fetch.JoinAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<UsageSnapshot?> FetchAndCacheAsync()
    {
        string? output = await Task.Run(async () =>
        {
            if (!File.Exists(_scriptPath))
            {
                return null;
            }

            string? nodePath = AcpExecutableResolver.FindNodeOnPath(Environment.GetEnvironmentVariable("PATH"));
            if (nodePath is null)
            {
                return null;
            }

            return await RunNodeScriptAsync(nodePath, _scriptPath).ConfigureAwait(false);
        }).ConfigureAwait(false);

        UsageSnapshot? snapshot = output is null ? null : ParseSnapshot(output);
        if (snapshot is null)
        {
            return null;
        }

        lock (_cacheLock)
        {
            _cachedSnapshot = snapshot;
        }

        return snapshot;
    }

    private static async Task<string?> RunNodeScriptAsync(string nodePath, string scriptPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = ProcessArgumentEscaping.ToArgumentsString(new[] { scriptPath }),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.Environment["NODE_USE_ENV_PROXY"] = "1";

        bool started = false;
        try
        {
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) => exited.TrySetResult(true);
            started = process.Start();
            if (!started)
            {
                return null;
            }

            Task stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            Task<string> stdout = BoundedProcessOutput.ReadBoundedAsync(process.StandardOutput, _maxOutputCharacters);
            Task complete = Task.WhenAll(stderrDrain, stdout, exited.Task);
            _ = complete.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Task finished = await Task.WhenAny(complete, Task.Delay(_processTimeout)).ConfigureAwait(false);
            if (finished != complete)
            {
                return null;
            }

            await complete.ConfigureAwait(false);
            return await stdout.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (started)
            {
                TryKill(process);
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
        {
        }
    }

    private static UsageSnapshot? ParseSnapshot(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            JObject parsed = JObject.Parse(output);
            if (parsed["error"] is not null || parsed["limits"] is not JArray limitsArray)
            {
                return null;
            }

            var limits = new System.Collections.Generic.List<UsageLimit>(limitsArray.Count);
            foreach (JToken token in limitsArray)
            {
                if (token is not JObject limit)
                {
                    continue;
                }

                limits.Add(new UsageLimit
                {
                    Kind = limit["kind"]?.Value<string>() ?? "",
                    Group = limit["group"]?.Value<string>() ?? "",
                    Percent = UsageServiceRules.ClampPercent(limit["percent"]?.Value<int?>() ?? 0),
                    Severity = limit["severity"]?.Value<string>() ?? "normal",
                    ResetsAt = ReadResetsAt(limit["resetsAt"]),
                    ScopeLabel = limit["scopeLabel"]?.Type == JTokenType.String ? limit["scopeLabel"]!.Value<string>() : null,
                    IsActive = limit["isActive"]?.Value<bool?>() ?? false,
                });
            }

            return new UsageSnapshot { Limits = limits, FetchedAt = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) when (ex is JsonException || ex is FormatException ||
            ex is OverflowException || ex is InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a JSON token to extract a reset timestamp as a nullable DateTimeOffset.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <returns>The date time offset? result.</returns>
    private static DateTimeOffset? ReadResetsAt(JToken? token) =>
        UsageResetTimestamp.FromJsonValue((token as JValue)?.Value);
}
