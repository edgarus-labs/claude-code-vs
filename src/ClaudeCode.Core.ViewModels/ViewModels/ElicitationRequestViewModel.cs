using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ClaudeCode.Core.ViewModels;

/// <summary>A pending <c>elicitation/create</c> form (typically an AskUserQuestion prompt), mirroring
/// <see cref="PermissionRequestViewModel"/>'s role for permission requests.
/// <para>The whole form is agent-authored, i.e. untrusted: field count, option count, the length of
/// each display string and the total length of all of them together are bounded at construction (see
/// <see cref="MaxFields"/>, <see cref="MaxOptionsPerField"/>, <see cref="MaxDisplayTextLength"/> and
/// <see cref="MaxFormTextLength"/>) so a hostile or merely runaway <c>elicitation/create</c> cannot
/// turn into an unbounded ItemsControl and unbounded text layout on the UI thread.</para></summary>
public sealed class ElicitationRequestViewModel
{
    /// <summary>Most fields rendered from one agent-authored form; the rest are dropped, so they are
    /// neither shown nor answered.</summary>
    public const int MaxFields = 20;

    /// <summary>Most options rendered per field; the rest are dropped.</summary>
    public const int MaxOptionsPerField = 20;

    /// <summary>Longest single agent-authored display string (message, title, description, option
    /// label) kept verbatim; longer text is truncated with an ellipsis. Option values and field keys
    /// are never truncated - they travel back to the agent as identifiers, not as display text.</summary>
    public const int MaxDisplayTextLength = 4_000;

    /// <summary>Most agent-authored display text kept for one whole form, counting the message, every
    /// field title and description and every option label and description together.
    /// <see cref="MaxDisplayTextLength"/> bounds one string, which is not the same thing: a form that
    /// respects every other cap (<see cref="MaxFields"/> x <see cref="MaxOptionsPerField"/> options,
    /// each with a label and a description) still carries millions of characters, and the card renders
    /// all of it in wrapping TextBlocks inside a non-virtualizing ItemsControl - realized and
    /// line-broken synchronously on the UI thread, again on every tool-window resize. Text past this
    /// budget is replaced by an ellipsis instead of being laid out.</summary>
    public const int MaxFormTextLength = 20_000;

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

        // The message is spent first, so the prompt the user actually has to read is never the part
        // squeezed out by a form padded with 400 overlong option descriptions.
        var budget = new DisplayTextBudget();
        Message = budget.Truncate(message) ?? string.Empty;
        Fields = fields.Take(MaxFields).Select(field => new ElicitationFieldViewModel(field, budget)).ToList();
        TruncationNotice = DescribeWithheldFields(fields.Count - Fields.Count);
        _respond = respond;
        SubmitCommand = new RelayCommand(Submit);
        DeclineCommand = new RelayCommand(Decline);
    }

    public string Message { get; }

    public IReadOnlyList<ElicitationFieldViewModel> Fields { get; }

    /// <summary>One line telling the user that the form was cut down to <see cref="MaxFields"/>, or
    /// null when it was not - the card collapses the line on null. Dropping the excess is the bound
    /// that keeps a runaway form renderable, but <see cref="Submit"/> still answers
    /// <see cref="ElicitationAction.Accept"/>, which the protocol reads as "the user answered the
    /// form", so the user has to be able to see which form they are accepting on.</summary>
    public string? TruncationNotice { get; }

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

    private static string? DescribeWithheldFields(int withheld) => withheld switch
    {
        <= 0 => null,
        1 => "1 further question was not shown.",
        _ => $"{withheld} further questions were not shown.",
    };

    /// <summary>Spends one form's <see cref="MaxFormTextLength"/> allowance across that form's display
    /// strings, in the order they are constructed. Deliberately mutable and not thread-safe: one
    /// instance is created and fully consumed inside a single <see cref="ElicitationRequestViewModel"/>
    /// constructor call, which runs on the UI thread.</summary>
    internal sealed class DisplayTextBudget
    {
        private int _remaining = MaxFormTextLength;

        /// <summary>Bounds one agent-authored display string against both the per-string cap and what
        /// is left of the form's allowance. Returns null for null so an absent title or description
        /// stays absent rather than becoming an empty one - the card collapses those lines on null,
        /// and an empty string would give every field a blank line with margins instead.</summary>
        internal string? Truncate(string? text)
        {
            if (text is null)
            {
                return null;
            }

            int allowed = Math.Min(MaxDisplayTextLength, _remaining);
            if (text.Length <= allowed)
            {
                _remaining -= text.Length;
                return text;
            }

            _remaining -= allowed;
            return text.Substring(0, allowed) + "…";
        }
    }
}
