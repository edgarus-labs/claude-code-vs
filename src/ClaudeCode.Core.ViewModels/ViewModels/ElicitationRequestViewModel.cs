using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ClaudeCode.Core.ViewModels;

/// <summary>A pending <c>elicitation/create</c> form (typically an AskUserQuestion prompt), mirroring
/// <see cref="PermissionRequestViewModel"/>'s role for permission requests.
/// <para>The whole form is agent-authored, i.e. untrusted: field count, option count and every piece
/// of display text are bounded at construction (see <see cref="MaxFields"/>) so a hostile or merely
/// runaway <c>elicitation/create</c> cannot turn into an unbounded ItemsControl and unbounded text
/// layout on the UI thread.</para></summary>
public sealed class ElicitationRequestViewModel
{
    /// <summary>Most fields rendered from one agent-authored form; the rest are dropped, so they are
    /// neither shown nor answered.</summary>
    public const int MaxFields = 20;

    /// <summary>Most options rendered per field; the rest are dropped.</summary>
    public const int MaxOptionsPerField = 20;

    /// <summary>Longest agent-authored display string (message, title, description, option label)
    /// kept verbatim; longer text is truncated with an ellipsis. Option values and field keys are
    /// never truncated - they travel back to the agent as identifiers, not as display text.</summary>
    public const int MaxDisplayTextLength = 4_000;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoContent =
        new Dictionary<string, IReadOnlyList<string>>();

    private readonly Action<ElicitationAnswer> _respond;
    private bool _submitted;

    public ElicitationRequestViewModel(string message, IReadOnlyList<ElicitationField> fields, Action<ElicitationAnswer> respond)
    {
        if (fields is null)
        {
            throw new ArgumentNullException(nameof(fields));
        }

        if (respond is null)
        {
            throw new ArgumentNullException(nameof(respond));
        }

        Message = TruncateForDisplay(message) ?? string.Empty;
        Fields = fields.Take(MaxFields).Select(field => new ElicitationFieldViewModel(field)).ToList();
        _respond = respond;
        SubmitCommand = new RelayCommand(Submit);
        DeclineCommand = new RelayCommand(Decline);
    }

    public string Message { get; }

    public IReadOnlyList<ElicitationFieldViewModel> Fields { get; }

    public ICommand SubmitCommand { get; }

    /// <summary>Dismisses the form without answering it - the user's only way to refuse a question
    /// short of cancelling the whole turn.</summary>
    public ICommand DeclineCommand { get; }

    /// <summary>Answers with whatever was filled in (a field left entirely blank is simply omitted -
    /// submitting with nothing filled in models "skip", matching AskUserQuestion's own semantics).
    /// Idempotent: a second call (e.g. a popup-dismissed-by-losing-focus path racing an explicit
    /// Submit click) is a no-op, as is a call after <see cref="Decline"/>.</summary>
    public void Submit()
    {
        if (_submitted)
        {
            return;
        }

        _submitted = true;

        var content = new Dictionary<string, IReadOnlyList<string>>();
        foreach (ElicitationFieldViewModel field in Fields)
        {
            IReadOnlyList<string> values = field.Kind switch
            {
                ElicitationFieldKind.MultiSelect => field.Options.Where(option => option.IsSelected).Select(option => option.Value).ToList(),
                ElicitationFieldKind.SingleSelect => SingleSelectValue(field),
                _ => TextValue(field),
            };

            if (values.Count > 0)
            {
                content[field.Key] = values;
            }
        }

        _respond(new ElicitationAnswer(ElicitationAction.Accept, content));
    }

    /// <summary>Refuses the form outright. Distinct from submitting it blank: the protocol separates
    /// "accepted with nothing filled in" from "declined", so the agent can tell a skipped optional
    /// question from a user who does not want to answer at all. Shares <see cref="Submit"/>'s
    /// exactly-once gate.</summary>
    public void Decline()
    {
        if (_submitted)
        {
            return;
        }

        _submitted = true;
        _respond(new ElicitationAnswer(ElicitationAction.Decline, NoContent));
    }

    private static string[] SingleSelectValue(ElicitationFieldViewModel field)
    {
        // ElicitationFieldViewModel keeps at most one option selected for a single-select field, so
        // the first hit is the user's actual choice rather than the earliest option in wire order.
        ElicitationOptionViewModel? selected = field.Options.FirstOrDefault(option => option.IsSelected);
        return selected is null ? Array.Empty<string>() : new[] { selected.Value };
    }

    private static string[] TextValue(ElicitationFieldViewModel field)
    {
        string trimmed = field.TextValue.Trim();
        return trimmed.Length == 0 ? Array.Empty<string>() : new[] { trimmed };
    }

    /// <summary>Bounds one agent-authored display string. Returns null for null so an absent title or
    /// description stays absent rather than becoming an empty one.</summary>
    internal static string? TruncateForDisplay(string? text) =>
        text is null || text.Length <= MaxDisplayTextLength ? text : text.Substring(0, MaxDisplayTextLength) + "…";
}
