using ClaudeCode.Core.ViewModels;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class PlanDocumentProtocolTests
{
    [Theory]
    [InlineData("https://claudecode.plan/plan.html", true)]
    [InlineData("https://claudecode.plan/plan.html#fragment", true)]
    [InlineData("https://claudecode.plan/plan.html?x=1", true)]
    [InlineData("https://claudecode.plan/plan.html/../index.html", false)]
    [InlineData("https://claudecode.plan/plan.html/sub/page", false)]
    [InlineData("https://claudecode.plan/index.html", false)]
    [InlineData("https://claudecode.plan/plan.htmlX", false)]
    [InlineData("https://claudecode.transcript/index.html", false)]
    [InlineData("https://evil.example/plan.html", false)]
    [InlineData("file:///plan.html", false)]
    [InlineData("not a uri", false)]
    [InlineData(null, false)]
    public void IsPlanDocumentUri_AcceptsOnlyTheExactPlanPage(string? uri, bool expected)
    {
        Assert.Equal(expected, PlanDocumentProtocol.IsPlanDocumentUri(uri));
    }
}
