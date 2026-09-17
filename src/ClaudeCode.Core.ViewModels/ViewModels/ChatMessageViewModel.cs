using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace ClaudeCode.Core.ViewModels;

public sealed class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatRole role, string text = "")
    {
        Role = role;
        _text = text;
    }

    public ChatRole Role { get; }

    private string _text;

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public ObservableCollection<ToolCallCardViewModel> ToolCalls { get; } = new ObservableCollection<ToolCallCardViewModel>();

    public void AppendText(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        Text += chunk;
    }
}
