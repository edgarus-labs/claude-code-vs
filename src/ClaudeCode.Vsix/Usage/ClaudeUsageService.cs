using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Usage;

/// <summary>
/// Reports account usage/rate-limit status via a bundled Node helper script rather than reading
/// credentials in this process. This is a deliberate, narrow exception to AcpAuthService's "never
/// read credentials in the extension host" rule: the `claude` CLI has no subcommand that exposes
/// usage as JSON, so there is no way to delegate this to the trusted claude-agent-acp executable
/// the way auth status is delegated. Instead, Resources\Scripts\fetch-usage.cjs — a small, reviewed,
/// bundled script — is spawned as its own subprocess; it alone reads the OAuth token from
/// ~/.claude/.credentials.json and calls the (undocumented) usage endpoint. The extension host
/// process only ever sees the trimmed JSON that subprocess prints to stdout, never the token itself.
/// </summary>
internal sealed class ClaudeUsageService : IUsageService
{
    private static readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(10);
    private const int _maxOutputCharacters = 64 * 1024;

    private readonly string _scriptPath;
    private readonly object _cacheLock = new object();
    private UsageSnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public ClaudeUsageService(string scriptPath)
    {
        _scriptPath = scriptPath ?? throw new ArgumentNullException(nameof(scriptPath));
    }

    public async Task<UsageSnapshot?> GetUsageAsync(CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_cachedSnapshot is not null && DateTimeOffset.UtcNow - _cachedAt < _cacheDuration)
            {
                return _cachedSnapshot;
            }
        }

        // Everything below touches the filesystem and spawns a process: File.Exists on the script,
        // one File.Exists per fully-qualified PATH entry inside FindNodeOnPath (a single unreachable
        // UNC or mapped-drive entry blocks on an SMB timeout), then CreateProcess. Both callers reach
        // this from the UI thread - ChatViewModel's constructor fire-and-forgets the usage polling
        // loop while ChatPanelView is still being constructed, and the IsUsagePanelOpen setter does
        // the same - so none of it may run there. Same rule as ClaudeCodeConnectionFactory: hop to
        // the thread pool first.
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

            return await RunNodeScriptAsync(nodePath, _scriptPath, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        UsageSnapshot? snapshot = output is null ? null : ParseSnapshot(output);
        if (snapshot is null)
        {
            return null;
        }

        lock (_cacheLock)
        {
            _cachedSnapshot = snapshot;
            _cachedAt = DateTimeOffset.UtcNow;
        }

        return snapshot;
    }

    private static async Task<string?> RunNodeScriptAsync(string nodePath, string scriptPath, CancellationToken cancellationToken)
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

        bool started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            _ = complete.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Task finished = await Task.WhenAny(complete, Task.Delay(_processTimeout, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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
            // Already exited or no longer accessible.
        }
    }

    private static UsageSnapshot? ParseSnapshot(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        // IUsageService promises null rather than an exception for unavailable data, so the
        // projection has to be inside the try too: the values are whatever the endpoint returned, and
        // Value<int?>/Value<bool?> raise OverflowException for a number outside int range (1e308
        // passes the helper's `typeof === "number"` filter untouched), FormatException or
        // InvalidCastException for a token that will not convert.
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
                    Percent = limit["percent"]?.Value<int?>() ?? 0,
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

    /// <summary>Reads a limit's reset timestamp. <see cref="JObject.Parse(string)"/> materializes a
    /// well-formed ISO-8601 timestamp as <see cref="JTokenType.Date"/>, so the reader's own value -
    /// not the token's string form - is what has to be normalized.</summary>
    private static DateTimeOffset? ReadResetsAt(JToken? token) =>
        UsageResetTimestamp.FromJsonValue((token as JValue)?.Value);
}
