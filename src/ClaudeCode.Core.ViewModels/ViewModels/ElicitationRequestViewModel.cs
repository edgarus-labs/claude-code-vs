using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Globalization;
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
public sealed class ElicitationRequestViewModel : ObservableObject
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

    private static readonly ElicitationFieldViewModel[] NoFields = Array.Empty<ElicitationFieldViewModel>();

    private readonly Action<ElicitationAnswer> _respond;
    private readonly RelayCommand _backCommand;
    private readonly RelayCommand _nextCommand;
    private readonly List<IReadOnlyList<ElicitationFieldViewModel>> _steps;
    private bool _submitted;
    private int _currentStep;

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
        _steps = GroupIntoSteps(Fields);
        TruncationNotice = DescribeWithheldFields(fields.Count - Fields.Count);
        _respond = respond;
        SubmitCommand = new RelayCommand(Submit);
        DeclineCommand = new RelayCommand(Decline);
        _backCommand = new RelayCommand(GoBack, () => CanGoBack);
        _nextCommand = new RelayCommand(GoNext, () => CanGoNext);
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

    /// <summary>The fields of the one question the card shows, in wire order. A multi-question form
    /// is stepped through rather than rendered as one tall wall of questions; empty (never null)
    /// only when the form has no fields at all.</summary>
    public IReadOnlyList<ElicitationFieldViewModel> CurrentStepFields =>
        _currentStep < _steps.Count ? _steps[_currentStep] : NoFields;

    /// <summary>1-based position of the current question, or 0 when the form has no fields.</summary>
    public int CurrentStepNumber => _steps.Count == 0 ? 0 : _currentStep + 1;

    /// <summary>How many questions the form asks, which is not the field count: one question is a
    /// choice field plus the free-text companions that follow it.</summary>
    public int StepCount => _steps.Count;

    public bool HasMultipleSteps => _steps.Count > 1;

    /// <summary>"Question 2 of 3" for a stepped form, or null when there is nothing to step through -
    /// the card collapses the line on null rather than showing "Question 1 of 1".</summary>
    public string? StepLabel => HasMultipleSteps
        ? string.Format(CultureInfo.InvariantCulture, "Question {0} of {1}", CurrentStepNumber, StepCount)
        : null;

    public bool CanGoBack => _currentStep > 0;

    public bool CanGoNext => _currentStep + 1 < _steps.Count;

    /// <summary>True when the card should offer Send: the user is on the last question, or there is
    /// no question to answer at all.</summary>
    public bool IsOnLastStep => !CanGoNext;

    /// <summary>Steps back one question. Disabled on the first question, and a no-op if executed
    /// there anyway.</summary>
    public ICommand BackCommand => _backCommand;

    /// <summary>Steps forward one question. Disabled on the last question, and a no-op if executed
    /// there anyway.</summary>
    public ICommand NextCommand => _nextCommand;

    private void GoBack() => MoveTo(_currentStep - 1);

    private void GoNext() => MoveTo(_currentStep + 1);

    /// <summary>Groups the (already bounded and truncated) fields into the questions the user is
    /// actually asked. One AskUserQuestion question arrives as several schema properties: the choice
    /// field, then an optional free-text "Other" companion, so a field-per-page card would ask a
    /// two-question form as four questions. Key-agnostic on purpose - the companion's key and title
    /// are agent-authored, so the shape of the form, not its wording, decides: a choice field opens a
    /// question, a text field joins the open one, and a text field with no question open (a text-only
    /// form, or text preceding the first choice) opens one itself so that no field is ever dropped.</summary>
    private static List<IReadOnlyList<ElicitationFieldViewModel>> GroupIntoSteps(IReadOnlyList<ElicitationFieldViewModel> fields)
    {
        var steps = new List<IReadOnlyList<ElicitationFieldViewModel>>();
        List<ElicitationFieldViewModel>? open = null;
        foreach (ElicitationFieldViewModel field in fields)
        {
            bool opensQuestion = field.Kind == ElicitationFieldKind.SingleSelect || field.Kind == ElicitationFieldKind.MultiSelect;
            if (opensQuestion || open is null)
            {
                open = new List<ElicitationFieldViewModel>();
                steps.Add(open);
            }

            open.Add(field);
        }

        return steps;
    }

    /// <summary>Clamps as well as guarding through <see cref="CanGoBack"/>/<see cref="CanGoNext"/>:
    /// <see cref="CurrentStepFields"/> indexes the step list directly, and a command invoked outside
    /// its button (a key gesture, a later view) must not be able to walk the position out of range -
    /// the resulting exception would surface inside a binding getter on the UI thread.</summary>
    private void MoveTo(int step)
    {
        if (step < 0 || step >= _steps.Count || step == _currentStep)
        {
            return;
        }

        _currentStep = step;
        OnPropertyChanged(nameof(CurrentStepFields));
        OnPropertyChanged(nameof(CurrentStepNumber));
        OnPropertyChanged(nameof(StepLabel));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsOnLastStep));
        _backCommand.NotifyCanExecuteChanged();
        _nextCommand.NotifyCanExecuteChanged();
    }

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

            // Cutting between the halves of a surrogate pair leaves a lone high surrogate that the
            // card renders as a replacement box before the ellipsis.
            if (allowed > 0 && char.IsHighSurrogate(text[allowed - 1]))
            {
                allowed--;
            }

            _remaining -= allowed;
            return text.Substring(0, allowed) + "…";
        }
    }
}
