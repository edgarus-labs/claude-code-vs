using ClaudeCode.Core.ViewModels;
using System.Collections.Generic;
using System.ComponentModel;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChatMessageViewModelTests
{
    [Fact]
    public void AppendText_MultipleChunks_ConcatenatesInOrder()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant);

        message.AppendText("Hello");
        message.AppendText(", ");
        message.AppendText("world!");

        Assert.Equal("Hello, world!", message.Text);
    }

    [Fact]
    public void AppendText_EmptyChunk_IsIgnored()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant, "seed");

        message.AppendText(string.Empty);

        Assert.Equal("seed", message.Text);
    }

    [Fact]
    public void AppendText_RaisesPropertyChangedForText()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)message).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        message.AppendText("chunk");

        Assert.Contains(nameof(ChatMessageViewModel.Text), raised);
    }
}
