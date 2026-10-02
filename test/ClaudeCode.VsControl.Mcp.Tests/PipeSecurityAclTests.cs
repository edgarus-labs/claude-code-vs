using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.VsControl.Mcp.Tests;

/// <summary>
/// Covers <see cref="PipeSecurityFactory.CreateCurrentUserOnly"/> by applying the resulting
/// <see cref="PipeSecurity"/> to a real Windows named pipe via <see cref="NamedPipeServerStreamAcl"/>
/// and asserting on the ACL the OS enforces.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeSecurityAclTests
{
    [Fact]
    public void CurrentUserOnlyPipeSecurity_GrantsExactlyOneAllowReadWriteRuleToTheCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = PipeSecurityFactory.CreateCurrentUserOnly(PipeAccessRights.ReadWrite);

        string pipeName = $"vscontrol-acl-test-{Guid.NewGuid():N}";
        using var pipe = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);

        var appliedSecurity = pipe.GetAccessControl();
        var rules = appliedSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        var userRule = Assert.Single(rules);
        Assert.Equal(user, userRule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, userRule.AccessControlType);
        Assert.Equal(PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, userRule.PipeAccessRights);
    }

    [Fact]
    public void CreateCurrentUserOnly_CalledRepeatedly_DoesNotLeakWindowsIdentityHandles()
    {
        for (int i = 0; i < 50; i++)
        {
            PipeSecurityFactory.CreateCurrentUserOnly(PipeAccessRights.ReadWrite);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var process = Process.GetCurrentProcess();
        long before = process.HandleCount;

        const int iterations = 1000;
        for (int i = 0; i < iterations; i++)
        {
            PipeSecurityFactory.CreateCurrentUserOnly(PipeAccessRights.ReadWrite);
        }

        process.Refresh();
        long after = process.HandleCount;
        long grown = after - before;

        Assert.True(grown < iterations / 2,
            $"Handle count grew by {grown} across {iterations} CreateCurrentUserOnly calls; expected far less than {iterations / 2} if WindowsIdentity is disposed correctly.");
    }
}
