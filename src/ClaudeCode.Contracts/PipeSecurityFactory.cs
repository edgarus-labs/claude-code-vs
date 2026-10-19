using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClaudeCode.Contracts;

/// <summary>
/// Builds the current-user-only ACL applied to the VsControl named pipe server stream (see
/// <c>VsControlPipeServer.CreatePipeSecurity</c> in the net48 Vsix host). Lives here rather than in
/// the net48 host so the net8.0 test suite can exercise the real production logic instead of
/// duplicating it (see <c>PipeSecurityAclTests</c>).
/// </summary>
public static class PipeSecurityFactory
{
    /// <summary>
    /// Creates a PipeSecurity object with access rules allowing only the current Windows user the specified pipe access rights.
    /// </summary>
    /// <param name="rights">The rights.</param>
    /// <returns>The pipe security result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when an error occurs during execution.</exception>
    public static PipeSecurity CreateCurrentUserOnly(PipeAccessRights rights)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("Unable to determine the current Windows user identity.");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, rights, AccessControlType.Allow));

        return security;
    }
}
