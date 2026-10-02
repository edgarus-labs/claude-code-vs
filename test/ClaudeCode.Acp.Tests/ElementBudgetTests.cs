using ClaudeCode.Contracts;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// Covers the node budget of the VSIX <c>getWindowElements</c> walk: <c>truncated</c> is reported
/// when a node beyond the budget is refused or truncation is marked, and not when the budget is
/// spent exactly.
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
        var budget = new ElementBudget(0);

        Assert.False(budget.TryTake());
        Assert.True(budget.Truncated);
    }

    [Fact]
    public void MarkTruncated_ReportsTruncationWithoutSpendingTheBudget()
    {
        var budget = new ElementBudget(1);

        budget.MarkTruncated();

        Assert.True(budget.Truncated);
        Assert.True(budget.TryTake());
    }
}
