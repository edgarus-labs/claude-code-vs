using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ClaudeCode.Core.ViewModels;

/// <summary>A pending <c>elicitation/create</c> form (typically an AskUserQuestion prompt), mirroring
/// <see cref="PermissionRequestViewModel"/>'s role for permission requests.</summary>
public sealed class ElicitationRequestViewModel
{
    private readonly Action<ElicitationAnswer> _respond;
    private bool _submitted;

    public ElicitationRequestViewModel(string message, IReadOnlyList<ElicitationField> fields, Action<ElicitationAnswer> respond)
    {
        if (respond is null)
        {
            throw new ArgumentNullException(nameof(respond));
        }

        Message = message;
        Fields = fields.Select(field => new ElicitationFieldViewModel(field)).ToList();
        _respond = respond;
        SubmitCommand = new RelayCommand(Submit);
    }

    public string Message { get; }

    public IReadOnlyList<ElicitationFieldViewModel> Fields { get; }

    public ICommand SubmitCommand { get; }

    /// <summary>Answers with whatever was filled in (a field left entirely blank is simply omitted -
    /// submitting with nothing filled in models "skip", matching AskUserQuestion's own semantics).
    /// Idempotent: a second call (e.g. a popup-dismissed-by-losing-focus path racing an explicit
    /// Submit click) is a no-op.</summary>
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

    private static string[] SingleSelectValue(ElicitationFieldViewModel field)
    {
        ElicitationOptionViewModel? selected = field.Options.FirstOrDefault(option => option.IsSelected);
        return selected is null ? Array.Empty<string>() : new[] { selected.Value };
    }

    private static string[] TextValue(ElicitationFieldViewModel field)
    {
        // WPF two-way binding can push null into the bound property; Submit runs inside a command
        // handler on the UI thread, where an unhandled exception tears down devenv.
        string trimmed = field.TextValue?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? Array.Empty<string>() : new[] { trimmed };
    }
}
