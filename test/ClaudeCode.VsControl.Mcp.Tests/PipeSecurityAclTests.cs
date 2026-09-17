using System;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace ClaudeCode.VsControl.Mcp.Tests;

/// <summary>
/// Pins the current-user-only ACL construction pattern that
/// <c>VsControlPipeServer.CreatePipeSecurity()</c> (src/ClaudeCode.Vsix/VsControl/VsControlPipeServer.cs,
/// net48) must use. That method lives in the VS-SDK host project, which requires the
/// Microsoft.VsSDK.BuildTools MSBuild targets to build and cannot be referenced from this lightweight
/// net8.0 xunit project without pulling in that toolchain - so this test exercises the identical
/// construction against the real Windows named-pipe ACL machinery (<see cref="NamedPipeServerStreamAcl"/>)
/// instead of the production type directly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeSecurityAclTests
{
    [Fact]
    public void CurrentUserOnlyPipeSecurity_GrantsExactlyOneAllowReadWriteRuleToTheCurrentUser()
    {
        var owner = WindowsIdentity.GetCurrent().Owner!;
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        string pipeName = $"vscontrol-acl-test-{Guid.NewGuid():N}";
        using var pipe = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);

        var appliedSecurity = pipe.GetAccessControl();
        var rules = appliedSecurity.GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(r => r.IdentityReference == owner)
            .ToList();

        var ownerRule = Assert.Single(rules);
        Assert.Equal(AccessControlType.Allow, ownerRule.AccessControlType);
        Assert.True(ownerRule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
    }
}
