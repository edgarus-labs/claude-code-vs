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
/// trip takes seconds, so the whole judgment - resolving the adapter and every attempt - is bounded
/// by one <see cref="DefaultTimeout"/> (a slow first run leaves the retries less time).
/// </summary>
public sealed class ClaudeCliEffortJudge : IEffortClassifier
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public const string JudgeModel = "haiku";
    private const int ParseRetries = 2;
    // A reply longer than this is discarded whole by the bounded reader, so it reads as an empty one.
    // The CLI is given no output cap of its own: a capped reply that runs over makes it fail instead
    // of returning the label it already wrote first.
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
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The judgment deadline must be positive.");
    }

    public async Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken)
    {
        if (prompt is null) throw new ArgumentNullException(nameof(prompt));
        return await JudgeAsync(EffortJudgePrompt.RenderUser(prompt), cancellationToken).ConfigureAwait(false);
    }

    // Judges an already rendered user turn. ClassifyAsync bounds a message to a few KB, so this is
    // also how a test hands the judge more than any pipe buffer holds.
    internal async Task<EffortLevel> JudgeAsync(string user, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var executable = await WithinDeadlineAsync(_resolveExecutable(deadline.Token), deadline.Token).ConfigureAwait(false);
            string reply = string.Empty;
            for (int attempt = 0; attempt <= ParseRetries; attempt++)
            {
                var system = attempt == 0 ? EffortJudgePrompt.SystemPrompt : EffortJudgePrompt.RetrySystemPrompt;
                reply = await RunAsync(executable, system, user, deadline.Token).ConfigureAwait(false);
                if (EffortJudgePrompt.ParseReply(reply) is { } level) return level;
            }
            throw new InvalidDataException("The effort judge replied without a level after three attempts.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(string.Format(CultureInfo.InvariantCulture,
                "The effort judge did not answer within {0:0} s.", _timeout.TotalSeconds));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Neither the caller nor the deadline cancelled: something under the judge did (the host,
            // while resolving the adapter). That is a failed judgment, not the caller's cancellation.
            throw new InvalidOperationException("The effort judge was cancelled by its host before it answered.", exception);
        }
    }

    // Resolving the adapter probes the filesystem in code that cannot observe the token (an
    // unreachable path blocks until the OS gives up), so the deadline is enforced by not waiting for
    // it rather than by asking it to stop.
    private static async Task<T> WithinDeadlineAsync<T>(Task<T> work, CancellationToken deadline)
    {
        var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (deadline.Register(() => expired.TrySetResult(true)))
        {
            if (await Task.WhenAny(work, expired.Task).ConfigureAwait(false) != work)
            {
                // Whatever the abandoned work ends in is nobody's to handle: keep its fault from
                // surfacing as an unobserved task exception.
                _ = work.ContinueWith(finished => finished.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                deadline.ThrowIfCancellationRequested();
            }
        }
        return await work.ConfigureAwait(false);
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
            // Outside any workspace, so the workspace's own instruction files are not picked up. The
            // user's own settings are excluded by --setting-sources, but user-level memory (such as a
            // home-directory CLAUDE.md) is not known to be, so the verdict is not proven uncoloured.
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Reasoning off, as oh-my-pi's judge (disableReasoning): with Haiku's default thinking a
        // judgment took 22-42 s in the measurements for PR #50, against about 4 s without it.
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
            try
            {
                process.Start();
            }
            catch
            {
                process.Dispose();
                throw;
            }
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

            // Off the awaiting path and under the same deadline as the waits below: Windows hands out
            // a synchronous pipe, so a CLI that stalls before reading its stdin would otherwise block
            // this write past the timeout (disposing the job in finally breaks the pipe). A CLI that
            // exits before reading it breaks the pipe instead; its exit code and output explain why.
            var bytes = Utf8.GetBytes(user);
            var writing = Task.Run(() =>
            {
                using (input) input.Write(bytes, 0, bytes.Length);
            }, CancellationToken.None);
            _ = writing.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await ProcessExitWait.WaitForExitAsync(writing, cancellationToken).ConfigureAwait(false);
            var inputRefused = false;
            try
            {
                await writing.ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Broken pipe: a non-zero exit code below explains it.
                inputRefused = true;
            }

            await ProcessExitWait.WaitForExitAsync(drained, cancellationToken).ConfigureAwait(false);
            var exited = Task.Run(process.WaitForExit, CancellationToken.None);
            // Observed like the tasks above: Dispose in finally may race an abandoned wait.
            _ = exited.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await ProcessExitWait.WaitForExitAsync(exited, cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                // CLI output can echo the user's message (including secrets). Keep the exit code,
                // but never expose stderr or stdout in an exception surfaced to the UI or logs.
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The effort judge exited with code {0}.", process.ExitCode));
            }
            // Exit 0 with a broken pipe: it never read the message, so the reply below answers nothing.
            if (inputRefused) throw new InvalidOperationException("The effort judge exited before reading the message.");
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
                // Dispose must run whatever the kill does, or the handle leaks and the kill's own
                // failure would replace the outcome being reported.
                try { Terminate(process); }
                finally { process.Dispose(); }
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
        catch (Win32Exception)
        {
            // Exited between the check and the kill, or cannot be killed by this user: either way
            // this runs in a finally, where throwing would replace the outcome being reported.
        }
    }

}
