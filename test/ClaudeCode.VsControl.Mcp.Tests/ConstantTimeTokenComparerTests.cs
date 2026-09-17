using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.VsControl.Mcp.Tests;

public sealed class ConstantTimeTokenComparerTests
{
    [Fact]
    public void Equals_WithIdenticalTokens_ReturnsTrue()
    {
        Assert.True(ConstantTimeTokenComparer.Equals("abc123XYZ", "abc123XYZ"));
    }

    [Fact]
    public void Equals_WithDifferentTokensOfSameLength_ReturnsFalse()
    {
        Assert.False(ConstantTimeTokenComparer.Equals("abc123XYZ", "abc123XYY"));
    }

    [Fact]
    public void Equals_WithDifferentLengths_ReturnsFalse()
    {
        Assert.False(ConstantTimeTokenComparer.Equals("short", "much-longer-token"));
    }

    [Fact]
    public void Equals_WithNullReceived_ReturnsFalse()
    {
        Assert.False(ConstantTimeTokenComparer.Equals(null, "expected-token"));
    }

    [Fact]
    public void Equals_WithEmptyReceivedAgainstNonEmptyExpected_ReturnsFalse()
    {
        Assert.False(ConstantTimeTokenComparer.Equals(string.Empty, "expected-token"));
    }

    [Fact]
    public void Equals_WithBothEmpty_ReturnsTrue()
    {
        Assert.True(ConstantTimeTokenComparer.Equals(string.Empty, string.Empty));
    }
}
