using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Auth;

/// <summary>
/// Reports native Claude authentication without reading credentials or owning a login flow.
/// The adapter's bundled CLI inherits the environment (including CLAUDE_CONFIG_DIR) and owns
/// credential lookup and refresh. An inconclusive status probe must not prevent a session attempt.
/// </summary>
internal sealed class AcpAuthService : IAcpAuthService
{
    private const string _loginInstructions = AcpAuthCommandOutcomes.LoginInstructions;
    private const string _adapterMissingMessage = AcpAuthCommandOutcomes.AdapterMissingMessage;
    private const string _logSource = "Claude Code";
    private static readonly TimeSpan _logoutTimeout = TimeSpan.FromSeconds(30);
    private readonly Func<string?> _adapterPathProvider;
    private readonly object _stateLock = new object();
    private AuthState _currentState = AuthState.Unknown;

    public AcpAuthService(Func<string?> adapterPathProvider)
    {
        _adapterPathProvider = adapterPathProvider ?? throw new ArgumentNullException(nameof(adapterPathProvider));
    }

    public AuthState CurrentState
    {
        get { lock (_stateLock) { return _currentState; } }
    }

    public event EventHandler<AuthStateChangedEventArgs>? StateChanged;

    public async Task<bool> IsSignedInAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (executable is null)
        {
            SetState(AuthState.Unknown, _adapterMissingMessage + " Native sign-in has not been checked.");
            return false;
        }

        AuthState state = await Task.Run(() => ReadNativeStatusAsync(executable, cancellationToken), cancellationToken).ConfigureAwait(false);
        SetState(state, state switch
        {
            AuthState.Unknown => "Native sign-in status could not be determined. You can still attempt a session; the native CLI handles credentials and refresh. " + _loginInstructions,
            AuthState.SignedOut => _loginInstructions,
            AuthState.Error => "Native sign-in status check failed: the CLI returned an unexpected response. " + _loginInstructions,
            _ => null,
        });
        return state == AuthState.SignedIn;
    }

    public async Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetState(AuthState.SigningIn);
        progress?.Report("Checking native Claude sign-in status; no credentials are read or changed by the extension.");
        try
        {
            if (await IsSignedInAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            SetState(AuthState.Unknown);
            throw;
        }
        finally
        {
            // An unexpected (non-cancellation) exception from IsSignedInAsync must not leave the
            // extension stuck reporting SigningIn forever.
            if (CurrentState == AuthState.SigningIn)
            {
                SetState(AuthState.Unknown);
            }
        }

        string detail = CurrentState switch
        {
            AuthState.SignedOut => "The native CLI reports no configured authentication. " + _loginInstructions,
            AuthState.Error => "Native sign-in status check failed: the CLI returned an unexpected response. " + _loginInstructions,
            _ => "Native sign-in status is unavailable. Verify the ACP executable path and run 'claude-agent-acp --cli auth status --json' in a terminal. " + _loginInstructions,
        };
        SetState(CurrentState, detail);
        throw new InvalidOperationException(detail);
    }

    public Task SignOutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Only reset this extension's displayed state; never modify shared native credentials.
        SetState(AuthState.SignedOut, "Visual Studio sign-in state cleared. Native Claude credentials have not been changed.");
        return Task.CompletedTask;
    }

    public async Task<AuthCommandOutcome> LaunchInteractiveLoginAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (executable is null)
        {
            return AcpAuthCommandOutcomes.AdapterMissing;
        }

        progress?.Report("Opening a console window to sign in — finish in the browser that opens.");
        var arguments = AcpAuthCliArguments.Login(executable);
        // If cancelled, RunVisibleProcessAsync throws before the status re-probe below, so the
        // previous AuthState is kept; the caller reports the cancellation.
        int? exitCode = await Task.Run(() => RunVisibleProcessAsync(executable.FileName, arguments, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (exitCode is null)
        {
            return AcpAuthCommandOutcomes.LoginCouldNotStart;
        }

        await IsSignedInAsync(cancellationToken).ConfigureAwait(false);
        return AcpAuthCommandOutcomes.ForLogin(CurrentState, exitCode.Value);
    }

    public async Task<AuthCommandOutcome> LaunchInteractiveLogoutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (executable is null)
        {
            return AcpAuthCommandOutcomes.AdapterMissing;
        }

        var arguments = AcpAuthCliArguments.Logout(executable);
        // Logout needs no user interaction, so unlike login it is bounded.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_logoutTimeout);
        int? exitCode;
        try
        {
            exitCode = await Task.Run(() => RunHiddenProcessAsync(executable.FileName, arguments, timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ActivityLog.TryLogWarning(_logSource, "'auth logout' did not exit within " + _logoutTimeout.TotalSeconds + " seconds and was stopped.");
            return AcpAuthCommandOutcomes.LogoutTimedOut;
        }

        if (exitCode is null)
        {
            return AcpAuthCommandOutcomes.LogoutCouldNotStart;
        }

        await IsSignedInAsync(cancellationToken).ConfigureAwait(false);
        var outcome = AcpAuthCommandOutcomes.ForLogout(exitCode.Value, CurrentState);
        if (outcome.Succeeded)
        {
            SetState(AuthState.SignedOut, outcome.Message);
        }

        return outcome;
    }

    private async Task<AcpExecutableSpec?> ResolveExecutableAsync(CancellationToken cancellationToken)
    {
        // The options callback uses GetDialogPage, which is UI-thread affine.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        string? overridePath = _adapterPathProvider();
        return string.IsNullOrWhiteSpace(overridePath)
            ? AcpExecutableResolver.TryResolveDefault()
            : AcpExecutableResolver.TryResolve(overridePath!);
    }

    /// <summary>Returns the exit code, or null when the console could not be started (logged).</summary>
    private static async Task<int?> RunVisibleProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = ProcessArgumentEscaping.ToArgumentsString(arguments),
                UseShellExecute = false,
                CreateNoWindow = false,
            },
            EnableRaisingEvents = true,
        };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, __) => exited.TrySetResult(true);
        if (!TryStart(process, "sign-in console"))
        {
            return null;
        }

        try
        {
            await ProcessExitWait.WaitForExitAsync(exited.Task, cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            // Cancellation or any unexpected failure: never leave the console running. This runs on
            // the thread pool (the caller wraps this in Task.Run), never on the thread that cancelled.
            KillProcessTree(process);
        }
    }

    /// <summary>Returns the exit code, or null when the process could not be started (logged).
    /// stdout is discarded; a bounded stderr tail goes to the ActivityLog on failure, never to the UI.</summary>
    private static async Task<int?> RunHiddenProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, __) => exited.TrySetResult(true);
        if (!TryStart(process, "sign-out command"))
        {
            return null;
        }

        Task stdout = Task.CompletedTask;
        Task<string> stderr = Task.FromResult(string.Empty);
        try
        {
            // Drain both so the process cannot block on a full pipe.
            stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            stderr = ReadBoundedTailAsync(process.StandardError, 4 * 1024);
            await ProcessExitWait.WaitForExitAsync(exited.Task, cancellationToken).ConfigureAwait(false);
            int exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                Task finished = await Task.WhenAny(stderr, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
                string tail = finished == stderr && stderr.Status == TaskStatus.RanToCompletion
                    ? await stderr.ConfigureAwait(false) : string.Empty;
                ActivityLog.TryLogWarning(_logSource, "'auth logout' exited with code "
                    + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". stderr: " + tail);
            }

            return exitCode;
        }
        finally
        {
            KillProcessTree(process);
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            _ = stdout.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = stderr.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static bool TryStart(Process process, string description)
    {
        try
        {
            if (process.Start())
            {
                return true;
            }

            ActivityLog.TryLogError(_logSource, "The " + description + " could not be started: " + process.StartInfo.FileName);
        }
        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
        {
            ActivityLog.TryLogError(_logSource, "The " + description + " could not be started: " + process.StartInfo.FileName + ": " + ex);
        }

        return false;
    }

    private static async Task<string> ReadBoundedTailAsync(StreamReader reader, int maxCharacters)
    {
        var buffer = new char[1024];
        var output = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
        {
            output.Append(buffer, 0, count);
            if (output.Length > maxCharacters)
            {
                output.Remove(0, output.Length - maxCharacters);
            }
        }

        return output.ToString();
    }

    private static async Task<AuthState> ReadNativeStatusAsync(AcpExecutableSpec executable, CancellationToken cancellationToken)
    {
        var arguments = AcpAuthCliArguments.Status(executable);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable.FileName,
                Arguments = ProcessArgumentEscaping.ToArgumentsString(arguments),
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
                return AuthState.Unknown;
            }

            // Discard stderr and cap stdout; neither stream is ever published.
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            Task<string> output = ReadStatusOutputAsync(process.StandardOutput);
            Task complete = Task.WhenAll(stderr, output, exited.Task);
            // Closing pipes during cleanup can fault pending reads after the bounded wait ends.
            _ = complete.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Task finished = await Task.WhenAny(complete, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (finished != complete)
            {
                return AuthState.Unknown;
            }

            await complete.ConfigureAwait(false);
            // A logged-out CLI can exit 1 while still returning valid status JSON.
            var status = JObject.Parse(await output.ConfigureAwait(false));
            if (status["loggedIn"]?.Type != JTokenType.Boolean)
            {
                return AuthState.Unknown;
            }

            string? provider = status["apiProvider"]?.Type == JTokenType.String ? status["apiProvider"]!.Value<string>() : null;
            bool externalProvider = !string.IsNullOrEmpty(provider) && provider != "firstParty";
            bool apiKeyConfigured = status["apiKeySource"]?.Type == JTokenType.String
                && !string.IsNullOrEmpty(status["apiKeySource"]!.Value<string>());
            return status["loggedIn"]!.Value<bool>() || externalProvider || apiKeyConfigured
                ? AuthState.SignedIn : AuthState.SignedOut;
        }
        catch (JsonException)
        {
            // Unparsable output from the CLI is a hard failure, distinct from an inconclusive probe.
            return AuthState.Error;
        }
        catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is InvalidOperationException)
        {
            return AuthState.Unknown;
        }
        finally
        {
            if (started)
            {
                KillProcessTree(process);
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
            }
        }
    }

    private static async Task<string> ReadStatusOutputAsync(StreamReader reader)
    {
        const int maxCharacters = 64 * 1024;
        var buffer = new char[1024];
        var output = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maxCharacters)
            {
                throw new InvalidOperationException("Native authentication status exceeded the output limit.");
            }

            output.Append(buffer, 0, count);
        }

        return output.ToString();
    }

    // .NET Framework lacks Kill(entireProcessTree). Target only this freshly spawned process and
    // its native CLI child, never other Claude processes or user sessions.
    private static void KillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            using var cleanup = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                Arguments = "/PID " + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (cleanup is not null && !cleanup.WaitForExit(2000))
            {
                cleanup.Kill();
            }
        }
        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
        {
            // Usually the process exited between the check and the targeted cleanup; the direct
            // Kill below is the fallback. Logged so a genuinely failed kill is diagnosable.
            ActivityLog.TryLogWarning(_logSource, "Process-tree cleanup via taskkill failed: " + ex.Message);
        }
        finally
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
    }

    private void SetState(AuthState state, string? detail = null)
    {
        lock (_stateLock)
        {
            _currentState = state;
        }

        StateChanged?.Invoke(this, new AuthStateChangedEventArgs(state, detail));
    }
}
