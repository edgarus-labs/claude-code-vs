using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
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

        ElicitationAnswer? captured = null;
        var vm = new ElicitationRequestViewModel("Pick one", fields, answer => captured = answer);

        Assert.Equal(ElicitationRequestViewModel.MaxFields, vm.Fields.Count);
        Assert.Equal(ElicitationRequestViewModel.MaxOptionsPerField, vm.Fields[0].Options.Count);

        vm.Fields[0].Options[0].IsSelected = true;
        vm.SubmitCommand.Execute(null);

        // A dropped field is never rendered, so it is answered as blank rather than silently
        // carrying a value the user never saw.
        Assert.False(captured!.Content.ContainsKey($"q{ElicitationRequestViewModel.MaxFields}"));
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

        vm.Fields[0].Options[0].IsSelected = true;
        vm.SubmitCommand.Execute(null);

        // The wire value is the agent's identifier, not display text - truncating it would answer
        // with an option the agent never offered.
        Assert.Equal(new[] { "red" }, captured!.Content["q0"]);
    }
}
