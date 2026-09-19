using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System.Collections.Generic;
using Xunit;

namespace ClaudeCode.Core.Tests;

/// <summary>The kill switches the plan document window relies on: it gates its "Send review" button
/// on <c>ReviewCommand.CanExecute</c>, so these guards are the only thing standing between a dead or
/// un-rejectable plan and a prompt injected into whatever session is live now.</summary>
public sealed class PlanReviewViewModelTests
{
    private static PermissionOption Option(string id, PermissionOutcome outcome) =>
        new() { OptionId = id, Label = id, Outcome = outcome };

    private static readonly IReadOnlyList<PermissionOption> BothOptions = new[]
    {
        Option("allow-once", PermissionOutcome.AllowOnce),
        Option("reject-once", PermissionOutcome.RejectOnce),
    };

    [Fact]
    public void ReviewCommand_WhenTheAgentOfferedNoRejectOption_CannotExecuteAndSendsNothing()
    {
        string? sent = null;
        var plan = new PlanReviewViewModel("# Plan", new[] { Option("allow-once", PermissionOutcome.AllowOnce) },
            _ => { }, comments => sent = comments);

        Assert.Null(plan.RejectOption);
        Assert.False(plan.ReviewCommand.CanExecute("please add a rollback step"));

        plan.ReviewCommand.Execute("please add a rollback step");

        Assert.Null(sent);
    }

    [Fact]
    public void ReviewCommand_AfterTheSessionAbandonedThePlan_CannotExecuteAndSendsNothing()
    {
        string? sent = null;
        var plan = new PlanReviewViewModel("# Plan", BothOptions, _ => { }, comments => sent = comments);
        Assert.True(plan.ReviewCommand.CanExecute("please add a rollback step"));

        plan.MarkResolved("Session ended");

        Assert.False(plan.ReviewCommand.CanExecute("please add a rollback step"));
        plan.ReviewCommand.Execute("please add a rollback step");
        Assert.Null(sent);
    }

    [Fact]
    public void ProceedCommand_AfterResolution_CannotExecuteAndDoesNotAnswerTwice()
    {
        var chosen = new List<string>();
        var plan = new PlanReviewViewModel("# Plan", BothOptions, option => chosen.Add(option.OptionId), _ => { });

        plan.ProceedCommand.Execute(null);
        plan.MarkResolved("Accepted — implementing…");
        plan.ProceedCommand.Execute(null);

        Assert.Equal(new[] { "allow-once" }, chosen);
        Assert.False(plan.ProceedCommand.CanExecute(null));
    }

    [Fact]
    public void ReviewCommand_BlankComments_CannotExecuteSoTheWindowKeepsTheTypedText()
    {
        var plan = new PlanReviewViewModel("# Plan", BothOptions, _ => { }, _ => { });

        Assert.False(plan.ReviewCommand.CanExecute("   "));
        Assert.False(plan.ReviewCommand.CanExecute(null));
    }
}
