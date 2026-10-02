using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClaudeCode.Contracts;

/// <summary>
/// Builds the current-user-only ACL applied to the VsControl named pipe server stream.
/// </summary>
public static class PipeSecurityFactory
{
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
