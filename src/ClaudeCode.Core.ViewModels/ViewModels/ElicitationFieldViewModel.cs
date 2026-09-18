using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;

namespace ClaudeCode.Core.ViewModels;

public sealed class ElicitationFieldViewModel : ObservableObject
{
    public ElicitationFieldViewModel(ElicitationField field)
    {
        Key = field.Key;
        Title = field.Title;
        Description = field.Description;
        Kind = field.Kind;
        Options = new ObservableCollection<ElicitationOptionViewModel>(field.Options.Select(option => new ElicitationOptionViewModel(option)));
    }

    public string Key { get; }

    public string? Title { get; }

    public string? Description { get; }

    public ElicitationFieldKind Kind { get; }

    public ObservableCollection<ElicitationOptionViewModel> Options { get; }

    private string _textValue = string.Empty;

    public string TextValue
    {
        get => _textValue;
        set => SetProperty(ref _textValue, value);
    }
}
