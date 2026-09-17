using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Contracts;

namespace ClaudeCode.Core.ViewModels.Demo
{
    /// <summary>Always-signed-in demo auth service so the standalone/design-time control shows the composer.</summary>
    public sealed class FakeAcpAuthService : IAcpAuthService
    {
        public AuthState CurrentState { get; private set; } = AuthState.SignedIn;

        public event EventHandler<AuthStateChangedEventArgs>? StateChanged;

        public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) => Task.FromResult(CurrentState == AuthState.SignedIn);

        public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
        {
            progress?.Report("Signed in (demo mode).");
            CurrentState = AuthState.SignedIn;
            StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedIn));
            return Task.CompletedTask;
        }

        public Task SignOutAsync(CancellationToken cancellationToken)
        {
            CurrentState = AuthState.SignedOut;
            StateChanged?.Invoke(this, new AuthStateChangedEventArgs(AuthState.SignedOut));
            return Task.CompletedTask;
        }
    }
}
