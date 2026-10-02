using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeCode.Core.Tests;

/// <summary>Covers the guards on <c>ReviewCommand.CanExecute</c>, which the plan document window uses
/// to enable its "Send review" button.</summary>
public sealed class PlanReviewViewModelTests
{
    private static PermissionOption Option(string id, PermissionOutcome outcome) =>
        new() { OptionId = id, Label = id, Outcome = outcome };

    private static readonly IReadOnlyList<PermissionOption> _bothOptions = new[]
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
        var plan = new PlanReviewViewModel("# Plan", _bothOptions, _ => { }, comments => sent = comments);
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
        var plan = new PlanReviewViewModel("# Plan", _bothOptions, option => chosen.Add(option.OptionId), _ => { });

        plan.ProceedCommand.Execute(null);
        plan.MarkResolved("Accepted — implementing…");
        plan.ProceedCommand.Execute(null);

        Assert.Equal(new[] { "allow-once" }, chosen);
        Assert.False(plan.ProceedCommand.CanExecute(null));
    }

    [Fact]
    public void ReviewCommand_BlankComments_CannotExecuteSoTheWindowKeepsTheTypedText()
    {
        var plan = new PlanReviewViewModel("# Plan", _bothOptions, _ => { }, _ => { });

        Assert.False(plan.ReviewCommand.CanExecute("   "));
        Assert.False(plan.ReviewCommand.CanExecute(null));
    }

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
        var plan = new PlanReviewViewModel("# Plan", _bothOptions, _ => { }, comments => sent = comments);

        plan.ReviewCommand.Execute("  add a rollback step \n");

        Assert.Equal("add a rollback step", sent);
    }

    [Fact]
    public void Markdown_BeyondTheRenderableLimit_IsBoundedBeforeThePlanWindowCanSerializeIt()
    {
        var oversized = new string('x', MarkdownSafetyLimits.MaxMarkdownLength + 1);

        var plan = new PlanReviewViewModel(oversized, _bothOptions, _ => { }, _ => { });

        Assert.Equal(
            MarkdownSafetyLimits.MaxMarkdownLength + MarkdownSafetyLimits._truncationNotice.Length,
            plan.Markdown.Length);
        Assert.EndsWith(MarkdownSafetyLimits._truncationNotice, plan.Markdown, StringComparison.Ordinal);
    }
}
