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
/// Classifies a message's effort level by asking Haiku the <see cref="EffortJudgePrompt"/> question
/// through the ACP adapter's bundled Claude Code CLI (<c>claude-agent-acp --cli -p</c>). The message is
/// sent on stdin. An unparseable reply is retried twice, and the whole judgment is bounded by one
/// <see cref="DefaultTimeout"/> or the timeout passed to the constructor.
/// </summary>
public sealed class ClaudeCliEffortJudge : IEffortClassifier
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public const string JudgeModel = "haiku";
    private const int _parseRetries = 2;
    private const int _maxReplyChars = 16 * 1024;
    private const int _maxDrainedStderrChars = 4 * 1024;
    private static readonly Encoding _utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<CancellationToken, Task<AcpExecutableSpec>> _resolveExecutable;
    private readonly TimeSpan _timeout;

    /// <param name="resolveExecutable">The adapter to run; faults when none is available.</param>
    public ClaudeCliEffortJudge(Func<CancellationToken, Task<AcpExecutableSpec>> resolveExecutable, TimeSpan? timeout = null)
    {
        _resolveExecutable = resolveExecutable ?? throw new ArgumentNullException(nameof(resolveExecutable));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The judgment deadline must be positive.");
        }
    }

    public async Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken)
    {
        if (prompt is null)
        {
            throw new ArgumentNullException(nameof(prompt));
        }

        var user = await Task.Run(() => EffortJudgePrompt.RenderUser(prompt), CancellationToken.None).ConfigureAwait(false);
        return await JudgeAsync(user, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<EffortLevel> JudgeAsync(string user, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var executable = await WithinDeadlineAsync(_resolveExecutable(deadline.Token), deadline.Token).ConfigureAwait(false);
            string reply = string.Empty;
            for (int attempt = 0; attempt <= _parseRetries; attempt++)
            {
                var system = attempt == 0 ? EffortJudgePrompt.SystemPrompt : EffortJudgePrompt.RetrySystemPrompt;
                reply = await RunAsync(executable, system, user, deadline.Token).ConfigureAwait(false);
                if (EffortJudgePrompt.ParseReply(reply) is { } level)
                {
                    return level;
                }
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
            throw new InvalidOperationException("The effort judge was cancelled by its host before it answered.", exception);
        }
    }

    private static async Task<T> WithinDeadlineAsync<T>(Task<T> work, CancellationToken deadline)
    {
        var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (deadline.Register(() => expired.TrySetResult(true)))
        {
            if (await Task.WhenAny(work, expired.Task).ConfigureAwait(false) != work)
            {
                _ = work.ContinueWith(finished => finished.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                deadline.ThrowIfCancellationRequested();
            }
        }
        return await work.ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a read‑only list of command‑line arguments for the specified executable, incorporating the provided system prompt and predefined options.
    /// </summary>
    /// <param name="executable">The executable.</param>
    /// <param name="system">The system.</param>
    /// <returns>A collection of iread only list items.</returns>
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

    /// <summary>
    /// Asynchronously executes the specified AcpExecutableSpec with system arguments, writes the user input to its standard input, captures and returns the bounded standard output while draining standard error.
    /// </summary>
    /// <param name="executable">The executable.</param>
    /// <param name="system">The system.</param>
    /// <param name="user">The user.</param>
    /// <param name="cancellationToken">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains the string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when an error occurs during execution.</exception>
    private static async Task<string> RunAsync(AcpExecutableSpec executable, string system, string user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.FileName,
            Arguments = ProcessArgumentEscaping.ToArgumentsString(Arguments(executable, system)),
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
            var reply = BoundedProcessOutput.ReadBoundedAsync(new StreamReader(output, _utf8), _maxReplyChars);
            var stderrDrain = BoundedProcessOutput.ReadBoundedAsync(new StreamReader(error, _utf8), _maxDrainedStderrChars);
            var drained = Task.WhenAll(reply, stderrDrain);
            _ = drained.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            var bytes = _utf8.GetBytes(user);
            var writing = Task.Run(() =>
            {
                using (input)
                {
                    input.Write(bytes, 0, bytes.Length);
                }
            }, CancellationToken.None);
            _ = writing.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await ProcessExitWait.WaitForExitAsync(writing, cancellationToken).ConfigureAwait(false);
            var inputRefused = false;
            try
            {
                await writing.ConfigureAwait(false);
            }
            catch (IOException)
            {
                inputRefused = true;
            }

            await ProcessExitWait.WaitForExitAsync(drained, cancellationToken).ConfigureAwait(false);
            var exited = Task.Run(process.WaitForExit, CancellationToken.None);
            _ = exited.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await ProcessExitWait.WaitForExitAsync(exited, cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The effort judge exited with code {0}.", process.ExitCode));
            }
            if (inputRefused)
            {
                throw new InvalidOperationException("The effort judge exited before reading the message.");
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
                try { Terminate(process); }
                finally { process.Dispose(); }
            }
        }
    }

    /// <summary>
    /// Terminates the specified process if it is still running, suppressing any exceptions that occur during termination.
    /// </summary>
    /// <param name="process">The process.</param>
    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

}
