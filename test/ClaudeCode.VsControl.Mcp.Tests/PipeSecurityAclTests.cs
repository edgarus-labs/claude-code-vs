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
/// Exercises the real production ACL construction (<see cref="PipeSecurityFactory.CreateCurrentUserOnly"/>,
/// src/ClaudeCode.Contracts/PipeSecurityFactory.cs) that <c>VsControlPipeServer.CreatePipeSecurity()</c>
/// (src/ClaudeCode.Vsix/VsControl/VsControlPipeServer.cs, net48) delegates to. The factory lives in
/// ClaudeCode.Contracts (netstandard2.0) specifically so this net8.0 xunit project - which cannot
/// reference the net48 VS-SDK host project without pulling in the Microsoft.VsSDK.BuildTools
/// toolchain - can call the actual production code path instead of duplicating it, applies the
/// resulting <see cref="PipeSecurity"/> to a real Windows named pipe via
/// <see cref="NamedPipeServerStreamAcl"/>, and asserts on the ACL the OS actually enforces.
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
        // Warm up: absorb JIT/first-call handle allocations so they don't pollute the measurement.
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

        // An undisposed WindowsIdentity per call leaks one native token handle per iteration, so a
        // leak would grow roughly proportionally to `iterations`. Tolerate generous background
        // handle noise (GC, other threads) without requiring growth to be exactly zero.
        Assert.True(grown < iterations / 2,
            $"Handle count grew by {grown} across {iterations} CreateCurrentUserOnly calls; expected far less than {iterations / 2} if WindowsIdentity is disposed correctly.");
    }
}
