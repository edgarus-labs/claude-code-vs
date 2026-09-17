using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Contracts;

public interface IAcpAuthService
{
    AuthState CurrentState { get; }

    event EventHandler<AuthStateChangedEventArgs> StateChanged;

    Task<bool> IsSignedInAsync(CancellationToken cancellationToken);

    Task SignInAsync(CancellationToken cancellationToken, IProgress<string>? progress = null);

    Task SignOutAsync(CancellationToken cancellationToken);
}
