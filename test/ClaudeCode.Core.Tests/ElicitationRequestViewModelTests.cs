using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
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
}
