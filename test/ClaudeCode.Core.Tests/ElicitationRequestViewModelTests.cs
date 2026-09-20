using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ElicitationRequestViewModelTests
{
    private static ElicitationField SingleSelectField(string key) => new ElicitationField(key, "Color", null, ElicitationFieldKind.SingleSelect,
    [
        new ElicitationOption("red", "Red"),
        new ElicitationOption("blue", "Blue"),
    ]);

    private static ElicitationField MultiSelectField(string key) => new ElicitationField(key, "Sides", null, ElicitationFieldKind.MultiSelect,
    [
        new ElicitationOption("left", "Left"),
        new ElicitationOption("right", "Right"),
    ]);

    private static ElicitationField TextField(string key) => new ElicitationField(key, "Other", null, ElicitationFieldKind.Text, []);

    [Fact]
    public void Submit_CollectsSelectedOptionAndTypedText_OmitsFieldsLeftBlank()
    {
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Pick one", [SingleSelectField("q0"), MultiSelectField("q1"), TextField("q1_custom")],
            answer => captured = answer);

        vm.Fields[0].Options[1].IsSelected = true; // blue
        vm.Fields[1].Options[0].IsSelected = true; // left
        // q1_custom left blank.

        vm.SubmitCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(ElicitationAction.Accept, captured!.Action);
        Assert.Equal(new[] { "blue" }, captured.Content["q0"]);
        Assert.Equal(new[] { "left" }, captured.Content["q1"]);
        Assert.False(captured.Content.ContainsKey("q1_custom"));
    }

    [Fact]
    public void Submit_MultiSelect_CollectsAllCheckedOptionsInFieldOrder()
    {
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Pick sides", [MultiSelectField("q0")], answer => captured = answer);

        vm.Fields[0].Options[0].IsSelected = true;
        vm.Fields[0].Options[1].IsSelected = true;

        vm.SubmitCommand.Execute(null);

        Assert.Equal(new[] { "left", "right" }, captured!.Content["q0"]);
    }

    [Fact]
    public void Submit_TextField_UsesTypedValue()
    {
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Anything else?", [TextField("q0")], answer => captured = answer);

        vm.Fields[0].TextValue = "  a custom answer  ";

        vm.SubmitCommand.Execute(null);

        Assert.Equal(new[] { "a custom answer" }, captured!.Content["q0"]);
    }

    [Fact]
    public void Submit_TextValueSetToNull_DoesNotThrowAndOmitsField()
    {
        // WPF two-way binding on an empty TextBox can push null into the bound property; Submit runs
        // on the UI thread inside a command handler, where an unhandled exception kills devenv.
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Anything else?", [TextField("q0")], answer => captured = answer);

        vm.Fields[0].TextValue = null!;

        vm.SubmitCommand.Execute(null);

        Assert.Equal(ElicitationAction.Accept, captured!.Action);
        Assert.Empty(captured.Content);
    }

    [Fact]
    public void Submit_EverythingLeftBlank_StillAcceptsWithEmptyContent()
    {
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Skip me", [SingleSelectField("q0"), TextField("q0_custom")], answer => captured = answer);

        vm.SubmitCommand.Execute(null);

        Assert.Equal(ElicitationAction.Accept, captured!.Action);
        Assert.Empty(captured.Content);
    }

    [Fact]
    public void Submit_CalledTwice_OnlyInvokesRespondOnce()
    {
        var callCount = 0;
        var vm = new ElicitationRequestViewModel("Pick one", [TextField("q0")], _ => callCount++);

        vm.SubmitCommand.Execute(null);
        vm.SubmitCommand.Execute(null);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Submit_SingleSelectWithTwoOptionsSelected_AnswersWithTheLatestSelection()
    {
        // A SingleSelect field must hold a mutual-exclusion invariant: WPF RadioButtons inside an
        // ItemsControl each sit in their own ContentPresenter and so group independently, which
        // leaves the view free to hand the view-model two checked options for one question.
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Pick one", [SingleSelectField("q0")], answer => captured = answer);

        vm.Fields[0].Options[0].IsSelected = true; // red
        vm.Fields[0].Options[1].IsSelected = true; // blue - what the user actually clicked last

        Assert.False(vm.Fields[0].Options[0].IsSelected);

        vm.SubmitCommand.Execute(null);

        Assert.Equal(new[] { "blue" }, captured!.Content["q0"]);
    }

    [Fact]
    public void TextValue_SetToNull_ReadsBackAsEmpty()
    {
        var vm = new ElicitationRequestViewModel("Anything else?", [TextField("q0")], _ => { });

        vm.Fields[0].TextValue = null!;

        Assert.Equal(string.Empty, vm.Fields[0].TextValue);
    }

    [Fact]
    public void Constructor_NullFields_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ElicitationRequestViewModel("Pick one", null!, _ => { }));
    }

    [Fact]
    public void Submit_DuplicateFieldKeys_AnswersWithTheLastFilledInField()
    {
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Pick one", [TextField("q0"), TextField("q0")], answer => captured = answer);

        vm.Fields[0].TextValue = "first";
        vm.Fields[1].TextValue = "second";

        vm.SubmitCommand.Execute(null);

        Assert.Equal(new[] { "second" }, captured!.Content["q0"]);
    }

    [Fact]
    public void Submit_SelectFieldWithNoOptions_OmitsTheFieldRatherThanAnsweringBlank()
    {
        ElicitationAnswer? captured = null;
        var unanswerable = new ElicitationField("q0", "Color", null, ElicitationFieldKind.SingleSelect, []);
        var vm = new ElicitationRequestViewModel("Pick one", [unanswerable], answer => captured = answer);

        vm.SubmitCommand.Execute(null);

        Assert.Equal(ElicitationAction.Accept, captured!.Action);
        Assert.False(captured.Content.ContainsKey("q0"));
    }

    [Fact]
    public void Submit_UnknownFieldKind_FallsBackToTheTypedText()
    {
        ElicitationAnswer? captured = null;
        var unknownKind = new ElicitationField("q0", "Mystery", null, (ElicitationFieldKind)999, []);
        var vm = new ElicitationRequestViewModel("Answer", [unknownKind], answer => captured = answer);

        vm.Fields[0].TextValue = "typed";

        vm.SubmitCommand.Execute(null);

        Assert.Equal(new[] { "typed" }, captured!.Content["q0"]);
    }

    [Fact]
    public void Decline_RespondsWithDeclineAndNoContent()
    {
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Pick one", [TextField("q0")], answer => captured = answer);

        vm.Fields[0].TextValue = "typed but dismissed";

        vm.DeclineCommand.Execute(null);

        Assert.Equal(ElicitationAction.Decline, captured!.Action);
        Assert.Empty(captured.Content);
    }

    [Fact]
    public void Submit_AfterDecline_DoesNotAnswerASecondTime()
    {
        var answers = new List<ElicitationAnswer>();
        var vm = new ElicitationRequestViewModel("Pick one", [TextField("q0")], answers.Add);

        vm.DeclineCommand.Execute(null);
        vm.SubmitCommand.Execute(null);

        Assert.Equal(ElicitationAction.Decline, Assert.Single(answers).Action);
    }

    [Fact]
    public void Decline_AfterSubmit_DoesNotAnswerASecondTime()
    {
        var answers = new List<ElicitationAnswer>();
        var vm = new ElicitationRequestViewModel("Pick one", [TextField("q0")], answers.Add);

        vm.SubmitCommand.Execute(null);
        vm.DeclineCommand.Execute(null);

        Assert.Equal(ElicitationAction.Accept, Assert.Single(answers).Action);
    }

    [Fact]
    public void Constructor_FormBeyondTheBounds_DropsTheExtraFieldsAndOptions()
    {
        var fields = new List<ElicitationField>();
        for (var i = 0; i < ElicitationRequestViewModel.MaxFields + 3; i++)
        {
            var options = new List<ElicitationOption>();
            for (var o = 0; o < ElicitationRequestViewModel.MaxOptionsPerField + 3; o++)
            {
                options.Add(new ElicitationOption($"v{o}", $"Option {o}"));
            }

            fields.Add(new ElicitationField($"q{i}", "Pick", null, ElicitationFieldKind.SingleSelect, options));
        }

        var vm = new ElicitationRequestViewModel("Pick one", fields, _ => { });

        Assert.Equal(ElicitationRequestViewModel.MaxFields, vm.Fields.Count);
        Assert.Equal(ElicitationRequestViewModel.MaxOptionsPerField, vm.Fields[0].Options.Count);
    }

    [Fact]
    public void Constructor_MoreFieldsThanTheCap_SaysHowManyQuestionsWereWithheld()
    {
        // Dropping the excess is the right denial-of-service defence, but Submit still answers
        // Accept, which in the protocol means "the user answered the form". The user has to be able
        // to see that they are accepting on a form they were not shown in full.
        var fields = new List<ElicitationField>();
        for (var i = 0; i < ElicitationRequestViewModel.MaxFields + 3; i++)
        {
            fields.Add(TextField($"q{i}"));
        }

        var vm = new ElicitationRequestViewModel("Pick one", fields, _ => { });

        Assert.Contains("3", vm.TruncationNotice);
    }

    [Fact]
    public void Constructor_FormWithinTheCaps_HasNoTruncationNotice()
    {
        // Null rather than empty: the card collapses the notice line through NullToVisibilityConverter.
        var vm = new ElicitationRequestViewModel("Pick one", [TextField("q0")], _ => { });

        Assert.Null(vm.TruncationNotice);
    }

    [Fact]
    public void Constructor_NullFieldInsideTheForm_ThrowsArgumentNullException()
    {
        // Construction runs inside ChatViewModel's UI-thread post, where an unhandled
        // NullReferenceException tears down devenv.
        Assert.Throws<ArgumentNullException>(() => new ElicitationRequestViewModel("Pick one", [null!], _ => { }));
    }

    [Fact]
    public void Constructor_AbsentTitleDescriptionAndOptionDescription_StayNull()
    {
        // The card collapses these lines only on null; an empty string would give every field a blank
        // bold line and every option a blank subtitle, both with margins.
        var field = new ElicitationField("q0", null, null, ElicitationFieldKind.SingleSelect,
        [
            new ElicitationOption("red", "Red"),
        ]);

        var vm = new ElicitationRequestViewModel("Pick one", [field], _ => { });

        Assert.Null(vm.Fields[0].Title);
        Assert.Null(vm.Fields[0].Description);
        Assert.Null(vm.Fields[0].Options[0].Description);
    }

    [Fact]
    public void Constructor_FullFormOfOverlongStrings_BoundsTheWholeFormsDisplayText()
    {
        // MaxDisplayTextLength alone is a per-string cap: MaxFields x MaxOptionsPerField options, each
        // with a label and a description, still add up to millions of characters, and the card renders
        // every one of them in a wrapping TextBlock inside a non-virtualizing ItemsControl - realized
        // and line-broken synchronously on the UI thread, again on every tool-window resize.
        var huge = new string('x', ElicitationRequestViewModel.MaxDisplayTextLength);
        var fields = new List<ElicitationField>();
        for (var i = 0; i < ElicitationRequestViewModel.MaxFields; i++)
        {
            var options = new List<ElicitationOption>();
            for (var o = 0; o < ElicitationRequestViewModel.MaxOptionsPerField; o++)
            {
                options.Add(new ElicitationOption($"v{o}", huge, huge));
            }

            fields.Add(new ElicitationField($"q{i}", huge, huge, ElicitationFieldKind.MultiSelect, options));
        }

        var vm = new ElicitationRequestViewModel(huge, fields, _ => { });

        // The message is spent from the budget first: it is the prompt the user has to read, so a
        // form padded with overlong option text must not be what squeezes it down to "…".
        Assert.Equal(huge, vm.Message);

        int rendered = vm.Message.Length + vm.Fields.Sum(field =>
            (field.Title?.Length ?? 0) + (field.Description?.Length ?? 0)
            + field.Options.Sum(option => option.Label.Length + (option.Description?.Length ?? 0)));

        // One ellipsis marker per display string is allowed on top of the budget itself.
        int markers = 1 + (vm.Fields.Count * 2) + vm.Fields.Sum(field => field.Options.Count * 2);

        Assert.True(
            rendered <= ElicitationRequestViewModel.MaxFormTextLength + markers,
            $"The form carries {rendered} characters of agent-authored display text; the per-form budget is "
                + $"{ElicitationRequestViewModel.MaxFormTextLength}.");
    }

    // The cut lands at index MaxDisplayTextLength; a pair straddling it would be halved and the
    // card would render a replacement box before the ellipsis - reachable from an ordinary emoji.
    [Fact]
    public void Constructor_CuttingOverlongText_NeverLeavesHalfOfASurrogatePair()
    {
        var message = new string('a', ElicitationRequestViewModel.MaxDisplayTextLength - 1) + "\U0001F600" + new string('b', 40);

        var vm = new ElicitationRequestViewModel(message, [], _ => { });

        Assert.Equal(new string('a', ElicitationRequestViewModel.MaxDisplayTextLength - 1) + "…", vm.Message);
    }

    [Fact]
    public void Constructor_OverlongAgentText_IsTruncatedForDisplay()
    {
        var huge = new string('x', ElicitationRequestViewModel.MaxDisplayTextLength * 2);
        var field = new ElicitationField("q0", huge, huge, ElicitationFieldKind.SingleSelect,
        [
            new ElicitationOption("red", huge, huge),
        ]);

        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel(huge, [field], answer => captured = answer);

        Assert.True(vm.Message.Length <= ElicitationRequestViewModel.MaxDisplayTextLength + 1, $"Message length {vm.Message.Length}");
        Assert.True(vm.Fields[0].Title!.Length <= ElicitationRequestViewModel.MaxDisplayTextLength + 1, $"Title length {vm.Fields[0].Title!.Length}");
        Assert.True(vm.Fields[0].Description!.Length <= ElicitationRequestViewModel.MaxDisplayTextLength + 1, $"Description length {vm.Fields[0].Description!.Length}");
        Assert.True(vm.Fields[0].Options[0].Label.Length <= ElicitationRequestViewModel.MaxDisplayTextLength + 1, $"Label length {vm.Fields[0].Options[0].Label.Length}");
        Assert.True(vm.Fields[0].Options[0].Description!.Length <= ElicitationRequestViewModel.MaxDisplayTextLength + 1, $"Option description length {vm.Fields[0].Options[0].Description!.Length}");

        vm.Fields[0].Options[0].IsSelected = true;
        vm.SubmitCommand.Execute(null);

        // The wire value is the agent's identifier, not display text - truncating it would answer
        // with an option the agent never offered.
        Assert.Equal(new[] { "red" }, captured!.Content["q0"]);
    }

    [Fact]
    public void Constructor_ChoiceFieldsEachFollowedByAnOtherBox_GroupEachOtherBoxWithItsOwnQuestion()
    {
        // Claude sends one AskUserQuestion question as two schema properties: the choice field and
        // an optional free-text "Other" companion. The companion is part of its question, not a
        // question of its own, so a two-question form must page as "Question 1 of 2".
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("approach"), TextField("approach_other"), MultiSelectField("checks"), TextField("checks_other")],
            _ => { });

        Assert.Equal(2, vm.StepCount);
        Assert.True(vm.HasMultipleSteps);
        Assert.Equal(1, vm.CurrentStepNumber);
        Assert.Equal("Question 1 of 2", vm.StepLabel);
        Assert.Equal(new[] { vm.Fields[0], vm.Fields[1] }, vm.CurrentStepFields);
        Assert.False(vm.IsOnLastStep);

        vm.NextCommand.Execute(null);

        Assert.Equal(2, vm.CurrentStepNumber);
        Assert.Equal("Question 2 of 2", vm.StepLabel);
        Assert.Equal(new[] { vm.Fields[2], vm.Fields[3] }, vm.CurrentStepFields);
        Assert.True(vm.IsOnLastStep);
    }

    [Fact]
    public void Constructor_ChoiceFieldWithNoTrailingTextField_IsAQuestionOfExactlyOneField()
    {
        // "Other" is optional in the schema; a question sent without one must not borrow the next
        // question's fields.
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), MultiSelectField("q1")], _ => { });

        Assert.Equal(2, vm.StepCount);
        Assert.Equal(new[] { vm.Fields[0] }, vm.CurrentStepFields);

        vm.NextCommand.Execute(null);

        Assert.Equal(new[] { vm.Fields[1] }, vm.CurrentStepFields);
    }

    [Fact]
    public void Constructor_SeveralTextFieldsAfterOneChoiceField_AllBelongToThatQuestion()
    {
        // The grouping rule is "a choice field opens a question, text fields join the open one" -
        // not "a choice field plus exactly one companion".
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), TextField("q0_other"), TextField("q0_note"), TextField("q0_more")], _ => { });

        Assert.Equal(1, vm.StepCount);
        Assert.Equal(vm.Fields, vm.CurrentStepFields);
        Assert.Null(vm.StepLabel);
        Assert.True(vm.IsOnLastStep);
    }

    [Fact]
    public void Constructor_TextFieldsBeforeTheFirstChoiceField_StartTheFirstQuestionRatherThanBeingDropped()
    {
        var vm = new ElicitationRequestViewModel("Pick one",
            [TextField("intro"), TextField("intro2"), SingleSelectField("q0"), TextField("q0_other")], _ => { });

        Assert.Equal(2, vm.StepCount);
        Assert.Equal(new[] { vm.Fields[0], vm.Fields[1] }, vm.CurrentStepFields);

        vm.NextCommand.Execute(null);

        Assert.Equal(new[] { vm.Fields[2], vm.Fields[3] }, vm.CurrentStepFields);
    }

    [Fact]
    public void Constructor_AnyForm_PutsEveryFieldInExactlyOneStepInWireOrder()
    {
        // The real protection against a grouping bug: a question that falls between two steps is
        // never shown, and the user accepts a form they were not asked.
        var vm = new ElicitationRequestViewModel("Pick one",
            [
                TextField("intro"),
                SingleSelectField("q0"), TextField("q0_other"),
                MultiSelectField("q1"),
                MultiSelectField("q2"), TextField("q2_other"), TextField("q2_note"),
            ],
            _ => { });

        var walked = new List<ElicitationFieldViewModel>();
        for (var step = 0; step < vm.StepCount; step++)
        {
            walked.AddRange(vm.CurrentStepFields);
            vm.NextCommand.Execute(null);
        }

        Assert.Equal(vm.Fields, walked);
    }

    [Fact]
    public void Constructor_TextOnlyForm_IsOneQuestionWithNothingToStepThrough()
    {
        var vm = new ElicitationRequestViewModel("Anything else?", [TextField("q0"), TextField("q1")], _ => { });

        Assert.Equal(1, vm.StepCount);
        Assert.Equal(vm.Fields, vm.CurrentStepFields);
        Assert.False(vm.HasMultipleSteps);
        Assert.Null(vm.StepLabel);
        Assert.True(vm.IsOnLastStep);
        Assert.False(vm.BackCommand.CanExecute(null));
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Constructor_FormWithNoFields_HasNoQuestionAndNavigatesNowhere()
    {
        // A message-only form, or one whose fields the bounds dropped entirely: the card still has
        // to render its message and its Send button, and its ItemsControl must not bind to null.
        var vm = new ElicitationRequestViewModel("Just so you know", [], _ => { });

        Assert.NotNull(vm.CurrentStepFields);
        Assert.Empty(vm.CurrentStepFields);
        Assert.Equal(0, vm.CurrentStepNumber);
        Assert.Equal(0, vm.StepCount);
        Assert.False(vm.HasMultipleSteps);
        Assert.Null(vm.StepLabel);
        Assert.True(vm.IsOnLastStep);

        vm.NextCommand.Execute(null);
        vm.BackCommand.Execute(null);

        Assert.Empty(vm.CurrentStepFields);
        Assert.Equal(0, vm.CurrentStepNumber);
    }

    [Fact]
    public void Constructor_MultiStepForm_StartsOnTheFirstQuestion()
    {
        // The card shows one question at a time, so the view-model owns the position within the form.
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), MultiSelectField("q1"), TextField("q1_other")], _ => { });

        Assert.Equal(new[] { vm.Fields[0] }, vm.CurrentStepFields);
        Assert.Equal(1, vm.CurrentStepNumber);
        Assert.Equal(2, vm.StepCount);
        Assert.True(vm.HasMultipleSteps);
        Assert.False(vm.CanGoBack);
        Assert.True(vm.CanGoNext);
        Assert.False(vm.IsOnLastStep);
    }

    [Fact]
    public void NextAndBack_StepThroughTheFormOneQuestionAtATime()
    {
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), MultiSelectField("q1"), SingleSelectField("q2"), TextField("q2_other")], _ => { });

        vm.NextCommand.Execute(null);

        Assert.Equal(new[] { vm.Fields[1] }, vm.CurrentStepFields);
        Assert.Equal(2, vm.CurrentStepNumber);
        Assert.True(vm.CanGoBack);
        Assert.True(vm.CanGoNext);
        Assert.False(vm.IsOnLastStep);

        vm.NextCommand.Execute(null);

        Assert.Equal(new[] { vm.Fields[2], vm.Fields[3] }, vm.CurrentStepFields);
        Assert.Equal(3, vm.CurrentStepNumber);
        Assert.False(vm.CanGoNext);
        Assert.True(vm.IsOnLastStep);

        vm.BackCommand.Execute(null);

        Assert.Equal(new[] { vm.Fields[1] }, vm.CurrentStepFields);
        Assert.Equal(2, vm.CurrentStepNumber);
    }

    [Fact]
    public void NextOnTheLastStep_AndBackOnTheFirst_AreClampedRatherThanRunningOffTheForm()
    {
        // CanExecute keeps the buttons disabled, but a command reached any other way (a bound key
        // gesture, a view written later) must not walk the position out of range: CurrentStepFields
        // indexes the step list directly, and an IndexOutOfRangeException inside a binding getter
        // runs on the UI thread, where it tears down devenv.
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), TextField("q0_other"), MultiSelectField("q1")], _ => { });

        vm.BackCommand.Execute(null);

        Assert.Equal(1, vm.CurrentStepNumber);

        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);

        Assert.Equal(2, vm.CurrentStepNumber);
        Assert.Equal(new[] { vm.Fields[2] }, vm.CurrentStepFields);
    }

    [Fact]
    public void Next_RaisesPropertyChangedForEveryPropertyTheCardBinds()
    {
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), MultiSelectField("q1"), TextField("q1_other")], _ => { });
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.NextCommand.Execute(null);

        Assert.Contains(nameof(ElicitationRequestViewModel.CurrentStepFields), changed);
        Assert.Contains(nameof(ElicitationRequestViewModel.CurrentStepNumber), changed);
        Assert.Contains(nameof(ElicitationRequestViewModel.StepLabel), changed);
        Assert.Contains(nameof(ElicitationRequestViewModel.CanGoBack), changed);
        Assert.Contains(nameof(ElicitationRequestViewModel.CanGoNext), changed);
        Assert.Contains(nameof(ElicitationRequestViewModel.IsOnLastStep), changed);
    }

    [Fact]
    public void Next_RefreshesTheNavigationCommandsCanExecute()
    {
        // The card binds Back/Next to these commands; without a CanExecuteChanged the buttons stay
        // in their startup enablement and the user can click a no-op.
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), TextField("q0_other"), MultiSelectField("q1")], _ => { });
        var backRefreshed = 0;
        vm.BackCommand.CanExecuteChanged += (_, _) => backRefreshed++;

        Assert.False(vm.BackCommand.CanExecute(null));

        vm.NextCommand.Execute(null);

        Assert.True(backRefreshed > 0);
        Assert.True(vm.BackCommand.CanExecute(null));
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Constructor_SingleQuestionForm_HasNothingToStepThrough()
    {
        var vm = new ElicitationRequestViewModel("Pick one", [SingleSelectField("q0"), TextField("q0_other")], _ => { });

        Assert.Equal(vm.Fields, vm.CurrentStepFields);
        Assert.Equal(1, vm.CurrentStepNumber);
        Assert.Equal(1, vm.StepCount);
        Assert.False(vm.HasMultipleSteps);
        Assert.Null(vm.StepLabel);
        Assert.True(vm.IsOnLastStep);
        Assert.False(vm.BackCommand.CanExecute(null));
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void StepLabel_OnTheSecondOfThreeQuestions_ReadsAsQuestionTwoOfThree()
    {
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), MultiSelectField("q1"), TextField("q1_other"), SingleSelectField("q2")], _ => { });

        Assert.Equal("Question 1 of 3", vm.StepLabel);

        vm.NextCommand.Execute(null);

        Assert.Equal("Question 2 of 3", vm.StepLabel);
    }

    [Fact]
    public void Submit_WithoutPaging_StillAnswersEveryFieldOfTheForm()
    {
        // Grouping and paging are presentation only: the answer carries every field the user filled
        // in, including fields on a step they never navigated to.
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("approach"), TextField("approach_other"), MultiSelectField("checks"), TextField("checks_other")],
            answer => captured = answer);

        vm.Fields[0].Options[1].IsSelected = true; // blue, on the step the user is shown
        vm.Fields[3].TextValue = "typed on a step never visited";

        vm.SubmitCommand.Execute(null);

        Assert.Equal(ElicitationAction.Accept, captured!.Action);
        Assert.Equal(new[] { "blue" }, captured.Content["approach"]);
        Assert.Equal(new[] { "typed on a step never visited" }, captured.Content["checks_other"]);
        Assert.Equal(1, vm.CurrentStepNumber);
    }

    [Fact]
    public void Submit_AfterPaging_IsStillAnsweredExactlyOnce()
    {
        var answers = new List<ElicitationAnswer>();
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), TextField("q0_other"), MultiSelectField("q1")], answers.Add);

        vm.NextCommand.Execute(null);
        vm.SubmitCommand.Execute(null);
        vm.SubmitCommand.Execute(null);

        Assert.Equal(ElicitationAction.Accept, Assert.Single(answers).Action);
    }

    [Fact]
    public void Decline_AfterPaging_StillDeclines()
    {
        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Pick one",
            [SingleSelectField("q0"), TextField("q0_other"), MultiSelectField("q1")],
            answer => captured = answer);

        vm.NextCommand.Execute(null);
        vm.Fields[1].TextValue = "typed but dismissed";
        vm.DeclineCommand.Execute(null);

        Assert.Equal(ElicitationAction.Decline, captured!.Action);
        Assert.Empty(captured.Content);
    }
}
