using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeCode.Core.ViewModels;

/// <summary>One question of an <see cref="ElicitationRequestViewModel"/> form. Owns the selection
/// invariant for its options: a single-select field never holds more than one selected option.</summary>
public sealed class ElicitationFieldViewModel : ObservableObject
{
    internal ElicitationFieldViewModel(ElicitationField field, ElicitationRequestViewModel.DisplayTextBudget budget)
    {
        if (field is null)
        {
            throw new ArgumentNullException(nameof(field));
        }

        Key = field.Key;
        Title = budget.Truncate(field.Title);
        Description = budget.Truncate(field.Description);
        Kind = field.Kind;
        IReadOnlyList<ElicitationOption> options = field.Options ?? Array.Empty<ElicitationOption>();
        Options = options
            .Take(ElicitationRequestViewModel.MaxOptionsPerField)
            .Select(option => new ElicitationOptionViewModel(option, this, budget))
            .ToList();
    }

    /// <summary>
    /// Gets the key.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string? Title { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets the kind.
    /// </summary>
    public ElicitationFieldKind Kind { get; }

    /// <summary>Fixed for the lifetime of the form; only each option's <see cref="ElicitationOptionViewModel.IsSelected"/> changes.</summary>
    public IReadOnlyList<ElicitationOptionViewModel> Options { get; }

    private string _textValue = string.Empty;

    /// <summary>The free-text answer. Never null: a null assignment is stored as the empty string.</summary>
    public string TextValue
    {
        get => _textValue;
        set => SetProperty(ref _textValue, value ?? string.Empty);
    }

    internal void OnOptionSelected(ElicitationOptionViewModel selected)
    {
        if (Kind != ElicitationFieldKind.SingleSelect)
        {
            return;
        }

        foreach (ElicitationOptionViewModel option in Options)
        {
            if (!ReferenceEquals(option, selected))
            {
                option.IsSelected = false;
            }
        }
    }
}
