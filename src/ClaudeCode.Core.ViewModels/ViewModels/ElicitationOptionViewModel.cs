using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCode.Core.ViewModels;

/// <summary>One selectable option within an <see cref="ElicitationFieldViewModel"/>; bindable from
/// either a RadioButton (single-select) or a CheckBox (multi-select).</summary>
public sealed class ElicitationOptionViewModel : ObservableObject
{
    public ElicitationOptionViewModel(ElicitationOption option)
    {
        Value = option.Value;
        Label = option.Label;
        Description = option.Description;
    }

    public string Value { get; }

    public string Label { get; }

    public string? Description { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
