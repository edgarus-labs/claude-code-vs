using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text;

namespace ClaudeCode.Core.ViewModels;

public sealed class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatRole role, string text = "")
    {
        Role = role;
        _textBuilder = new StringBuilder(text);
    }

    public ChatRole Role { get; }

    private readonly StringBuilder _textBuilder;

    public string Text => _textBuilder.ToString();

    public ObservableCollection<ToolCallCardViewModel> ToolCalls { get; } = new ObservableCollection<ToolCallCardViewModel>();

    public void AppendText(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        _textBuilder.Append(chunk);
        OnPropertyChanged(nameof(Text));
    }
}
