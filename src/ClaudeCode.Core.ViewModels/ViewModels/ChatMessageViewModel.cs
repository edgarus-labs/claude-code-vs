using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCode.Core.ViewModels
{
    /// <summary>One chat bubble: a user turn, or a streaming/completed assistant turn.</summary>
    public sealed class ChatMessageViewModel : ObservableObject
    {
        public ChatMessageViewModel(ChatRole role, string text = "")
        {
            Role = role;
            _text = text;
        }

        public ChatRole Role { get; }

        private string _text;

        /// <summary>Streamed/complete message text. Mutates in place so the bound TextBlock updates live.</summary>
        public string Text
        {
            get => _text;
            private set => SetProperty(ref _text, value);
        }

        public ObservableCollection<ToolCallCardViewModel> ToolCalls { get; } = new ObservableCollection<ToolCallCardViewModel>();

        /// <summary>Appends one streamed chunk (agent message or thought text) to <see cref="Text"/>.</summary>
        public void AppendText(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            Text += chunk;
        }
    }
}
