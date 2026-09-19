using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// The node budget of the VSIX <c>getWindowElements</c> walk. <c>truncated</c> is the agent's only
/// signal that the tree it received is complete, and the previous defect - reporting truncation for
/// any exhausted budget - made a tree that fitted exactly look incomplete, so both halves of that
/// distinction are pinned here.
/// </summary>
public sealed class ElementBudgetTests
{
    [Fact]
    public void TryTake_BudgetSpentExactly_ReportsNothingTruncated()
    {
        var budget = new ElementBudget(3);

        Assert.True(budget.TryTake());
        Assert.True(budget.TryTake());
        Assert.True(budget.TryTake());

        Assert.False(budget.Truncated);
    }

    [Fact]
    public void TryTake_OneNodeBeyondTheBudget_IsRefusedAndReportsTruncated()
    {
        var budget = new ElementBudget(1);
        _ = budget.TryTake();

        Assert.False(budget.TryTake());
        Assert.True(budget.Truncated);
    }

    [Fact]
    public void TryTake_EmptyBudget_TruncatesOnTheFirstNode()
    {
        // maxNodes: 1 pre-charges the root and leaves nothing for descendants.
        var budget = new ElementBudget(0);

        Assert.False(budget.TryTake());
        Assert.True(budget.Truncated);
    }

    [Fact]
    public void MarkTruncated_ReportsTruncationWithoutSpendingTheBudget()
    {
        // The depth clamp drops children the node budget would still have paid for; the flag has to
        // record that without charging for a node that was never emitted.
        var budget = new ElementBudget(1);

        budget.MarkTruncated();

        Assert.True(budget.Truncated);
        Assert.True(budget.TryTake());
    }
}
