using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Core.Tests;

internal sealed class AlwaysSignedInAuthService : IAcpAuthService
{
    public AuthState CurrentState => AuthState.SignedIn;

    public event EventHandler<AuthStateChangedEventArgs>? StateChanged { add { } remove { } }

    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) => Task.CompletedTask;

    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
