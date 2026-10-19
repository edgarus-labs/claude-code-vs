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
    /// <summary>
    /// Initializes a new instance of the ElicitationFieldViewModel class with the specified field and display text budget, validating the field parameter and initializing properties with truncated text and options.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="budget">The budget.</param>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
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

    /// <summary>Never null: WPF two-way binding pushes null for an emptied TextBox, and this property
    /// is declared non-nullable, so the setter coerces rather than handing a null back to converters,
    /// validation predicates and the view - all of which run on the UI thread, where an
    /// unhandled NullReferenceException tears down devenv.</summary>
    public string TextValue
    {
        get => _textValue;
        set => SetProperty(ref _textValue, value ?? string.Empty);
    }

    /// <summary>Enforces single-selection. A RadioButton inside an ItemsControl sits in its own
    /// ContentPresenter and so does not group with its siblings; without this the view can leave two
    /// options checked for one question and the answer silently becomes the earliest one in wire
    /// order rather than what the user clicked.</summary>
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
