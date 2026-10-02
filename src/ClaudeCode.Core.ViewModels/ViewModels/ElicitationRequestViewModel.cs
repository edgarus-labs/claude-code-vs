using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace ClaudeCode.Core.ViewModels;

/// <summary>A pending <c>elicitation/create</c> form (typically an AskUserQuestion prompt).
/// Field count, option count, each display string and the total display text are bounded at
/// construction by <see cref="MaxFields"/>, <see cref="MaxOptionsPerField"/>,
/// <see cref="MaxDisplayTextLength"/> and <see cref="MaxFormTextLength"/>.</summary>
public sealed class ElicitationRequestViewModel : ObservableObject
{
    /// <summary>Most fields rendered from one form; the rest are dropped, so they are neither shown
    /// nor answered.</summary>
    public const int MaxFields = 20;

    /// <summary>Most options rendered per field; the rest are dropped.</summary>
    public const int MaxOptionsPerField = 20;

    /// <summary>Longest single display string (message, title, description, option label) kept
    /// verbatim; longer text is truncated with an ellipsis. Option values and field keys are never
    /// truncated.</summary>
    public const int MaxDisplayTextLength = 4_000;

    /// <summary>Most display text kept for one whole form, counting the message, every field title
    /// and description and every option label and description together. Text past this budget is
    /// replaced by an ellipsis.</summary>
    public const int MaxFormTextLength = 20_000;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _noContent =
        new Dictionary<string, IReadOnlyList<string>>();

    private static readonly ElicitationFieldViewModel[] _noFields = Array.Empty<ElicitationFieldViewModel>();

    private readonly Action<ElicitationAnswer> _respond;
    private readonly RelayCommand _backCommand;
    private readonly RelayCommand _nextCommand;
    private readonly List<IReadOnlyList<ElicitationFieldViewModel>> _steps;
    private bool _submitted;
    private int _currentStep;

    /// <summary>
    /// Initializes a new instance of the ElicitationRequestViewModel class, validating that fields and respond.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="fields">The collection of fields.</param>
    /// <param name="respond">The respond.</param>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
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

    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the collection of fields.
    /// </summary>
    public IReadOnlyList<ElicitationFieldViewModel> Fields { get; }

    /// <summary>One line telling the user that the form was cut down to <see cref="MaxFields"/>, or
    /// null when it was not.</summary>
    public string? TruncationNotice { get; }

    /// <summary>
    /// Gets the submit command.
    /// </summary>
    public ICommand SubmitCommand { get; }

    /// <summary>Dismisses the form without answering it.</summary>
    public ICommand DeclineCommand { get; }

    /// <summary>The fields of the current question, in wire order; empty (never null) only when the
    /// form has no fields at all.</summary>
    public IReadOnlyList<ElicitationFieldViewModel> CurrentStepFields =>
        _currentStep < _steps.Count ? _steps[_currentStep] : _noFields;

    /// <summary>1-based position of the current question, or 0 when the form has no fields.</summary>
    public int CurrentStepNumber => _steps.Count == 0 ? 0 : _currentStep + 1;

    /// <summary>How many questions the form asks. One question is a choice field plus the free-text
    /// companions that follow it.</summary>
    public int StepCount => _steps.Count;

    /// <summary>
    /// Gets a value indicating whether has multiple steps.
    /// </summary>
    public bool HasMultipleSteps => _steps.Count > 1;

    /// <summary>"Question 2 of 3" for a form with multiple questions, otherwise null.</summary>
    public string? StepLabel => HasMultipleSteps
        ? string.Format(CultureInfo.InvariantCulture, "Question {0} of {1}", CurrentStepNumber, StepCount)
        : null;

    /// <summary>
    /// Gets a value indicating whether can go back.
    /// </summary>
    public bool CanGoBack => _currentStep > 0;

    /// <summary>
    /// Gets a value indicating whether can go next.
    /// </summary>
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

    /// <summary>Answers with whatever was filled in; a field left entirely blank is omitted.
    /// Idempotent: a second call, or a call after <see cref="Decline"/>, is a no-op.</summary>
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

    /// <summary>Declines the form outright, which the agent receives as distinct from a blank
    /// submission. Shares <see cref="Submit"/>'s exactly-once gate.</summary>
    public void Decline()
    {
        if (_submitted)
        {
            return;
        }

        _submitted = true;
        _respond(new ElicitationAnswer(ElicitationAction.Decline, _noContent));
    }

    private static string[] SingleSelectValue(ElicitationFieldViewModel field)
    {
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

    internal sealed class DisplayTextBudget
    {
        private int _remaining = MaxFormTextLength;

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

            if (allowed > 0 && char.IsHighSurrogate(text[allowed - 1]))
            {
                allowed--;
            }

            _remaining -= allowed;
            return text.Substring(0, allowed) + "…";
        }
    }
}
