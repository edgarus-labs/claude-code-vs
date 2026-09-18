using ClaudeCode.Core.ViewModels;
using System;
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

    [Fact]
    public void AppendText_CrossingLimit_PreservesPrefixAndReportsTruncation()
    {
        var prefix = new string('x', MarkdownSafetyLimits.MaxMarkdownLength - 1);
        var message = new ChatMessageViewModel(ChatRole.Assistant, prefix);
        var notifications = 0;
        message.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMessageViewModel.Text))
            {
                notifications++;
            }
        };

        message.AppendText("yz");

        Assert.Equal(MarkdownSafetyLimits.LimitMarkdownLength(prefix + "yz"), message.Text);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void AppendText_AtExactLimit_OnlyTruncatesWhenMoreTextArrives()
    {
        var prefix = new string('x', MarkdownSafetyLimits.MaxMarkdownLength);
        var message = new ChatMessageViewModel(ChatRole.Assistant);
        message.AppendText(prefix);
        Assert.Equal(prefix, message.Text);

        message.AppendText("y");
        Assert.Equal(MarkdownSafetyLimits.LimitMarkdownLength(prefix + "y"), message.Text);
    }

    [Fact]
    public void Constructor_OversizedMessage_UsesTheSameBoundedTextAsStreaming()
    {
        var text = new string('x', MarkdownSafetyLimits.MaxMarkdownLength + 1);
        var message = new ChatMessageViewModel(ChatRole.Assistant, text);

        Assert.Equal(MarkdownSafetyLimits.LimitMarkdownLength(text), message.Text);
    }

    [Fact]
    public void AppendText_AfterTruncation_DoesNotNotifyOrCopyTheCappedText()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant);
        message.AppendText(new string('x', MarkdownSafetyLimits.MaxMarkdownLength + 1));
        var displayed = message.Text;
        var notifications = 0;
        message.PropertyChanged += (_, _) => notifications++;

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            message.AppendText("ignored");
            _ = message.Text;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, notifications);
        Assert.Equal(displayed, message.Text);
        Assert.True(allocated < 4096, $"Post-cap chunks allocated {allocated} bytes.");
    }
}
