using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Acp;

/// <summary>
/// Auto effort's judge: asks Haiku, through the ACP adapter's bundled Claude Code CLI
/// (<c>claude-agent-acp --cli -p</c>) and therefore the user's own Claude Code sign-in, the
/// <see cref="EffortJudgePrompt"/> question about one message. Tool-less, settings-less and not
/// persisted; the message goes in on stdin, never on the command line. Like oh-my-pi's text judge
/// it retries an unparseable reply twice; unlike its 4 s API call, a CLI start plus a model round
/// trip takes seconds, so the whole judgment is bounded by <see cref="DefaultTimeout"/>.
/// </summary>
public sealed class ClaudeCliEffortJudge : IEffortClassifier
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public const string JudgeModel = "haiku";
    private const int ParseRetries = 2;
    // Reasoning off, as oh-my-pi's judge (disableReasoning). With Haiku's default thinking one
    // judgment took 22-42 s; without it about 4 s (measured). No output cap: a capped reply that
    // runs over makes the CLI fail instead of returning the label it already wrote first.
    private const int MaxReplyChars = 16 * 1024;
    private const int MaxErrorChars = 4 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<CancellationToken, Task<AcpExecutableSpec>> _resolveExecutable;
    private readonly TimeSpan _timeout;

    /// <param name="resolveExecutable">The adapter to run; faults when none is available.</param>
    public ClaudeCliEffortJudge(Func<CancellationToken, Task<AcpExecutableSpec>> resolveExecutable, TimeSpan? timeout = null)
    {
        _resolveExecutable = resolveExecutable ?? throw new ArgumentNullException(nameof(resolveExecutable));
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken)
    {
        if (prompt is null) throw new ArgumentNullException(nameof(prompt));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var executable = await _resolveExecutable(deadline.Token).ConfigureAwait(false);
            var user = EffortJudgePrompt.RenderUser(prompt);
            string reply = string.Empty;
            for (int attempt = 0; attempt <= ParseRetries; attempt++)
            {
                var system = attempt == 0 ? EffortJudgePrompt.System : EffortJudgePrompt.RetrySystem;
                reply = await RunAsync(executable, system, user, deadline.Token).ConfigureAwait(false);
                if (EffortJudgePrompt.ParseReply(reply) is { } level) return level;
            }
            throw new InvalidDataException("The effort judge replied without a level: " + Excerpt(reply));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(string.Format(CultureInfo.InvariantCulture,
                "The effort judge did not answer within {0:0} s.", _timeout.TotalSeconds));
        }
    }

    internal static IReadOnlyList<string> Arguments(AcpExecutableSpec executable, string system)
    {
        var arguments = new List<string>(executable.Arguments)
        {
            "--cli", "-p",
            "--model", JudgeModel,
            "--system-prompt", system,
            "--tools", string.Empty,
            "--setting-sources", string.Empty,
            "--strict-mcp-config",
            "--no-session-persistence",
            "--max-turns", "1",
            "--output-format", "text",
        };
        return arguments;
    }

    private static async Task<string> RunAsync(AcpExecutableSpec executable, string system, string user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.FileName,
            Arguments = ProcessArgumentEscaping.ToArgumentsString(Arguments(executable, system)),
            // Outside any project, so no workspace instructions or settings can colour the verdict.
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MAX_THINKING_TOKENS"] = "0";

        WindowsJobProcess? job = null;
        Process process;
        Stream input, output, error;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // The job owns the whole tree: disposing it also ends anything the CLI spawned.
            job = WindowsJobProcess.Start(startInfo);
            process = job.Process;
            (input, output, error) = (job.StandardInput, job.StandardOutput, job.StandardError.BaseStream);
        }
        else
        {
            process = new Process { StartInfo = startInfo };
            process.Start();
            (input, output, error) = (process.StandardInput.BaseStream, process.StandardOutput.BaseStream, process.StandardError.BaseStream);
        }

        try
        {
            var reply = BoundedProcessOutput.ReadBoundedAsync(new StreamReader(output, Utf8), MaxReplyChars);
            var stderr = BoundedProcessOutput.ReadBoundedAsync(new StreamReader(error, Utf8), MaxErrorChars);
            // Observed even when the wait below is abandoned, so a late pipe fault is never unobserved.
            var drained = Task.WhenAll(reply, stderr);
            _ = drained.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            var bytes = Utf8.GetBytes(user);
            await input.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            input.Dispose();

            await ProcessExitWait.WaitForExitAsync(drained, cancellationToken).ConfigureAwait(false);
            await ProcessExitWait.WaitForExitAsync(Task.Run(process.WaitForExit, CancellationToken.None), cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                // API errors arrive on stdout with nothing on stderr.
                var details = await stderr.ConfigureAwait(false);
                if (details.Trim().Length == 0) details = await reply.ConfigureAwait(false);
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The effort judge exited with code {0}: {1}", process.ExitCode, Excerpt(details)));
            }
            return await reply.ConfigureAwait(false);
        }
        finally
        {
            if (job is not null)
            {
                job.Dispose();
            }
            else
            {
                Terminate(process);
                process.Dispose();
            }
        }
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException)
        {
            // No live process is associated any more.
        }
        catch (Win32Exception) when (process.HasExited)
        {
            // It exited between the check and the kill.
        }
    }

    private static string Excerpt(string text)
    {
        text = text.Trim();
        return text.Length <= 300 ? text : text.Substring(0, 300) + "…";
    }
}
