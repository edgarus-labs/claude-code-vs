using System;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class NodeRuntimePolicyTests
{
    [Theory]
    [InlineData("22.23.3", false)]
    [InlineData("22.23.4", true)]
    [InlineData("22.24.0", true)]
    [InlineData("22.99.99", true)]
    [InlineData("22.0.0", false)]
    [InlineData("24.22.0", false)]
    [InlineData("24.22.1", true)]
    [InlineData("24.23.0", true)]
    [InlineData("26.12.0", false)]
    [InlineData("26.12.1", true)]
    [InlineData("26.13.0", true)]
    [InlineData("20.19.5", false)]
    [InlineData("18.20.8", false)]
    [InlineData("23.11.1", false)]
    [InlineData("25.9.0", false)]
    [InlineData("27.0.0", true)]
    [InlineData("28.1.2", true)]
    [InlineData("v22.23.3", false)]
    [InlineData("v22.23.4", true)]
    [InlineData("v26.12.1\n", true)]
    [InlineData("  v24.22.0\r\n", false)]
    public void GetWarning_AppliesPatchedFloors(string versionText, bool supported)
    {
        string? warning = NodeRuntimePolicy.GetWarning(versionText);

        Assert.Equal(supported, warning is null);
    }

    [Fact]
    public void GetWarning_NamesDetectedVersionFixedFloorsAndAdvisory()
    {
        string? warning = NodeRuntimePolicy.GetWarning("v20.19.5");

        Assert.NotNull(warning);
        Assert.Contains("20.19.5", warning);
        Assert.Contains("22.23.4", warning);
        Assert.Contains("24.22.1", warning);
        Assert.Contains("26.12.1", warning);
        Assert.Contains("CVE-2026-111497", warning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("v22")]
    [InlineData("v22.23")]
    [InlineData("22.23.4.1")]
    [InlineData("v22.x.4")]
    [InlineData("v-1.2.3")]
    [InlineData("v22.23.4-rc.1")]
    [InlineData("node: command not found")]
    public void GetWarning_WhenVersionCannotBeDetermined_DoesNotWarn(string? versionText)
    {
        Assert.Null(NodeRuntimePolicy.GetWarning(versionText));
    }

    [Theory]
    [InlineData("v22.23.4", 22, 23, 4)]
    [InlineData("22.23.4", 22, 23, 4)]
    [InlineData("V24.22.1", 24, 22, 1)]
    [InlineData(" v26.12.1 ", 26, 12, 1)]
    public void TryParseVersion_AcceptsPlainAndPrefixedVersions(string text, int major, int minor, int patch)
    {
        Assert.True(NodeRuntimePolicy.TryParseVersion(text, out Version version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    public void TryParseVersion_RejectsMalformedInput(string? text)
    {
        Assert.False(NodeRuntimePolicy.TryParseVersion(text, out _));
    }
}
