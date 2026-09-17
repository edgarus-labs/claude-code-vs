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
    public static PipeSecurity CreateCurrentUserOnly(PipeAccessRights rights)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.Owner
            ?? throw new InvalidOperationException("Unable to determine the current Windows user identity.");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(owner, rights, AccessControlType.Allow));

        return security;
    }
}
