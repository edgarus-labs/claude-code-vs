using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System.Collections.Generic;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class PlanViewModelTests
{
    private static PlanEntry Entry(string content, PlanEntryStatus status) =>
        new() { Content = content, Status = status };

    [Fact]
    public void ProgressLabel_CountsOnlyCompletedEntriesOverTheWholePlan()
    {
        var plan = new PlanViewModel(new List<PlanEntry>
        {
            Entry("done", PlanEntryStatus.Completed),
            Entry("running", PlanEntryStatus.InProgress),
            Entry("done too", PlanEntryStatus.Completed),
            Entry("queued", PlanEntryStatus.Pending),
            Entry("also queued", PlanEntryStatus.Pending),
        });

        Assert.Equal(2, plan.CompletedCount);
        Assert.Equal(5, plan.TotalCount);
        Assert.Equal("2/5", plan.ProgressLabel);
    }

    [Fact]
    public void ProgressLabel_EmptyPlan_ReportsZeroOfZeroRatherThanDividingOrThrowing()
    {
        var plan = new PlanViewModel(new List<PlanEntry>());

        Assert.Equal("0/0", plan.ProgressLabel);
    }
}
