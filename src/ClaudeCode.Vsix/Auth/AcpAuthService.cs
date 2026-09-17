using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;

namespace ClaudeCode.Vsix.Auth
{
    /// <summary>
    /// Wraps the Claude Code CLI's own OAuth browser login (`claude setup-token`). Never handles a raw API
    /// key: it shells out to the CLI, scrapes the long-lived OAuth token the CLI itself prints/persists, and
    /// stores that token DPAPI-encrypted (current user scope) in a local file under
    /// <c>%LOCALAPPDATA%\ClaudeCodeVs\auth.bin</c>. The decrypted token is exposed only via
    /// <see cref="TryGetOauthToken"/> for injection into the agent child process's environment - it is never
    /// written to a command line or logged.
    /// </summary>
    internal sealed class AcpAuthService : IAcpAuthService
    {
        private static readonly Regex OauthTokenPattern = new Regex(@"sk-ant-oat01-[A-Za-z0-9\-_]+", RegexOptions.Compiled);

        private readonly Func<string> _cliPathProvider;
        private readonly string _authFilePath;
        private readonly object _stateLock = new object();
        private AuthState _currentState = AuthState.Unknown;

        /// <param name="cliPathProvider">Resolves the `claude` executable to invoke for `setup-token`, evaluated fresh on every sign-in attempt so a later change to the VS Options CLI path override takes effect immediately.</param>
        public AcpAuthService(Func<string> cliPathProvider)
        {
            _cliPathProvider = cliPathProvider ?? throw new ArgumentNullException(nameof(cliPathProvider));
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _authFilePath = Path.Combine(localAppData, "ClaudeCodeVs", "auth.bin");
        }

        public AuthState CurrentState
        {
            get { lock (_stateLock) { return _currentState; } }
        }

        public event EventHandler<AuthStateChangedEventArgs>? StateChanged;

        public Task<bool> IsSignedInAsync(CancellationToken cancellationToken)
        {
            var signedIn = TryGetOauthToken(out _);
            SetState(signedIn ? AuthState.SignedIn : AuthState.SignedOut);
            return Task.FromResult(signedIn);
        }

        public async Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
        {
            SetState(AuthState.SigningIn);

            var cliPath = _cliPathProvider();
            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "setup-token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            string? capturedToken = null;
            var outputLog = new StringBuilder();

            void OnLine(string? data)
            {
                if (data == null)
                {
                    return;
                }

                outputLog.AppendLine(data);
                progress?.Report(data);

                if (capturedToken == null)
                {
                    var match = OauthTokenPattern.Match(data);
                    if (match.Success)
                    {
                        capturedToken = match.Value;
                    }
                }
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                SetState(AuthState.Error, ex.Message);
                throw new InvalidOperationException($"Failed to start '{cliPath} setup-token': {ex.Message}", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (cancellationToken.Register(() =>
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
                    // Process already exited between the check and the kill attempt.
                }
            }))
            {
                await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (capturedToken == null)
            {
                // The token may have arrived split across OutputDataReceived callbacks; re-scan the full log.
                var match = OauthTokenPattern.Match(outputLog.ToString());
                if (match.Success)
                {
                    capturedToken = match.Value;
                }
            }

            if (process.ExitCode != 0 || capturedToken == null)
            {
                var detail = $"'{cliPath} setup-token' exited with code {process.ExitCode} without producing an OAuth token.";
                SetState(AuthState.Error, detail);
                throw new InvalidOperationException(detail);
            }

            PersistToken(capturedToken);
            SetState(AuthState.SignedIn);
        }

        public Task SignOutAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (File.Exists(_authFilePath))
                {
                    File.Delete(_authFilePath);
                }
            }
            catch (IOException)
            {
                // Best-effort delete; the file may be locked momentarily by another process.
            }

            SetState(AuthState.SignedOut);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Decrypts and returns the stored OAuth token, if any. Used by
        /// <see cref="ClaudeCode.Vsix.Connections.ClaudeCodeConnectionFactory"/> to populate the
        /// CLAUDE_CODE_OAUTH_TOKEN environment variable for the spawned agent process - never via a CLI argument.
        /// </summary>
        internal bool TryGetOauthToken(out string token)
        {
            token = string.Empty;
            try
            {
                if (!File.Exists(_authFilePath))
                {
                    return false;
                }

                var protectedBytes = File.ReadAllBytes(_authFilePath);
                var rawBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                token = Encoding.UTF8.GetString(rawBytes);
                return !string.IsNullOrWhiteSpace(token);
            }
            catch (Exception ex) when (ex is IOException || ex is CryptographicException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void PersistToken(string token)
        {
            var directory = Path.GetDirectoryName(_authFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var rawBytes = Encoding.UTF8.GetBytes(token);
            var protectedBytes = ProtectedData.Protect(rawBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_authFilePath, protectedBytes);
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
}
