using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
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

    // An agent that offers only the "always" variants still gets a Proceed and a Review button:
    // the outcome fallback is what keeps the plan window answerable on that option set.
    [Fact]
    public void Options_WithOnlyAlwaysVariants_FallBackToThemForProceedAndReview()
    {
        var chosen = new List<string>();
        var plan = new PlanReviewViewModel("# Plan",
            new[] { Option("allow-always", PermissionOutcome.AllowAlways), Option("reject-always", PermissionOutcome.RejectAlways) },
            option => chosen.Add(option.OptionId), _ => { });

        Assert.Equal("allow-always", plan.ProceedOption?.OptionId);
        Assert.Equal("reject-always", plan.RejectOption?.OptionId);
        Assert.True(plan.ReviewCommand.CanExecute("tighten the tests"));

        plan.ProceedCommand.Execute(null);

        Assert.Equal(new[] { "allow-always" }, chosen);
    }

    [Fact]
    public void ReviewCommand_SendsTheCommentsTrimmed()
    {
        string? sent = null;
        var plan = new PlanReviewViewModel("# Plan", BothOptions, _ => { }, comments => sent = comments);

        plan.ReviewCommand.Execute("  add a rollback step \n");

        Assert.Equal("add a rollback step", sent);
    }

    [Fact]
    public void Markdown_BeyondTheRenderableLimit_IsBoundedBeforeThePlanWindowCanSerializeIt()
    {
        // PlanDocumentView.Render() serialises Markdown straight into an ExecuteScriptAsync payload,
        // so the agent-sized plan has to be cut here or nowhere.
        var oversized = new string('x', MarkdownSafetyLimits.MaxMarkdownLength + 1);

        var plan = new PlanReviewViewModel(oversized, BothOptions, _ => { }, _ => { });

        Assert.Equal(
            MarkdownSafetyLimits.MaxMarkdownLength + MarkdownSafetyLimits.TruncationNotice.Length,
            plan.Markdown.Length);
        Assert.EndsWith(MarkdownSafetyLimits.TruncationNotice, plan.Markdown, StringComparison.Ordinal);
    }
}
