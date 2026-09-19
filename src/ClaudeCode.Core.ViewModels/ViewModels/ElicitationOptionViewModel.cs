using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace ClaudeCode.Core.ViewModels;

/// <summary>One selectable option within an <see cref="ElicitationFieldViewModel"/>; bindable from
/// either a RadioButton (single-select) or a CheckBox (multi-select). Constructed only by its
/// owning field, which enforces the single-select invariant when this option is selected.</summary>
public sealed class ElicitationOptionViewModel : ObservableObject
{
    private readonly ElicitationFieldViewModel _owner;

    internal ElicitationOptionViewModel(ElicitationOption option, ElicitationFieldViewModel owner, ElicitationRequestViewModel.DisplayTextBudget budget)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Value = option.Value;
        Label = budget.Truncate(option.Label) ?? string.Empty;
        Description = budget.Truncate(option.Description);
    }

    /// <summary>The identifier sent back to the agent - never truncated for display.</summary>
    public string Value { get; }

    public string Label { get; }

    public string? Description { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
            {
                _owner.OnOptionSelected(this);
            }
        }
    }
}
