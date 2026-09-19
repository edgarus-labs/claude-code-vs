using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
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
}
