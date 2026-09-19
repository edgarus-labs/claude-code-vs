using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeCode.Core.ViewModels;

/// <summary>An implementation plan the agent wants approved before leaving plan mode (ACP tool call
/// kind <c>switch_mode</c>). "Proceed" answers the permission request with its allow option; "Review"
/// answers with the reject option and sends the reviewer's comments back to the agent as the next prompt.</summary>
public sealed class PlanReviewViewModel : ObservableObject
{
    private bool _isResolved;
    private string _status = string.Empty;

    public PlanReviewViewModel(string markdown, IReadOnlyList<PermissionOption> options, Action<PermissionOption> choose, Action<string> review)
    {
        Markdown = markdown ?? string.Empty;
        Options = options ?? Array.Empty<PermissionOption>();
        ProceedOption = Options.FirstOrDefault(option => option.Outcome == PermissionOutcome.AllowOnce)
            ?? Options.FirstOrDefault(option => option.Outcome == PermissionOutcome.AllowAlways);
        RejectOption = Options.FirstOrDefault(option => option.Outcome == PermissionOutcome.RejectOnce)
            ?? Options.FirstOrDefault(option => option.Outcome == PermissionOutcome.RejectAlways);
        ProceedCommand = new RelayCommand(() =>
        {
            if (ProceedOption is not null && !IsResolved) choose(ProceedOption);
        }, () => ProceedOption is not null && !IsResolved);
        ReviewCommand = new RelayCommand<string>(comments =>
        {
            if (RejectOption is null || IsResolved || string.IsNullOrWhiteSpace(comments)) return;
            review(comments!.Trim());
        }, comments => RejectOption is not null && !IsResolved && !string.IsNullOrWhiteSpace(comments));
    }

    public string Markdown { get; }

    public IReadOnlyList<PermissionOption> Options { get; }

    public PermissionOption? ProceedOption { get; }

    public PermissionOption? RejectOption { get; }

    public IRelayCommand ProceedCommand { get; }

    public IRelayCommand<string> ReviewCommand { get; }

    public bool IsResolved
    {
        get => _isResolved;
        private set
        {
            if (SetProperty(ref _isResolved, value))
            {
                ProceedCommand.NotifyCanExecuteChanged();
                ReviewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>"Accepted — implementing…" / "Sent back for revision" once answered.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    internal void MarkResolved(string status)
    {
        Status = status;
        IsResolved = true;
    }
}
