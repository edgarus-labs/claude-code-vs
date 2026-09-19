using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed class ChatMessageViewModelTests
{
    private static ToolCallCardViewModel MakeCard(string id) =>
        new ToolCallCardViewModel(new ToolCallUpdate { ToolCallId = id, Title = id, Status = ToolCallStatus.Completed });


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

    [Fact]
    public void Parts_ConsecutiveAppendTextCalls_MergeIntoOneTextPart()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant);

        message.AppendText("Hello");
        message.AppendText(", world!");

        var part = Assert.Single(message.Parts);
        var textPart = Assert.IsType<ChatTextPart>(part);
        Assert.Equal("Hello, world!", textPart.Text);
    }

    [Fact]
    public void Parts_ToolCallBetweenTwoTextRuns_PreservesChronologicalOrder()
    {
        // Reproduces a real report: the UI rendered "all text, then all tool calls" regardless of
        // when the tool call actually happened, making it look like the assistant wrote its whole
        // answer before running anything - Parts is what fixes that, so this pins the order.
        var message = new ChatMessageViewModel(ChatRole.Assistant);
        var card = MakeCard("tc-1");

        message.AppendText("Let me check that.");
        message.AppendToolCall(card);
        message.AppendText("Found it.");

        Assert.Collection(message.Parts,
            part => Assert.Equal("Let me check that.", Assert.IsType<ChatTextPart>(part).Text),
            part => Assert.Same(card, Assert.IsType<ChatToolCallPart>(part).Card),
            part => Assert.Equal("Found it.", Assert.IsType<ChatTextPart>(part).Text));
    }

    [Fact]
    public void Parts_TextAfterToolCall_StartsANewTextPart_NotMergedWithTextBeforeTheCall()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant);

        message.AppendText("before");
        message.AppendToolCall(MakeCard("tc-1"));
        message.AppendText("after");

        var textParts = message.Parts.OfType<ChatTextPart>().ToList();
        Assert.Equal(2, textParts.Count);
        Assert.Equal("before", textParts[0].Text);
        Assert.Equal("after", textParts[1].Text);
    }

    [Fact]
    public void Parts_TwoToolCallsInARow_BothAppearAsSeparateParts()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant);
        var first = MakeCard("tc-1");
        var second = MakeCard("tc-2");

        message.AppendToolCall(first);
        message.AppendToolCall(second);

        Assert.Collection(message.Parts,
            part => Assert.Same(first, Assert.IsType<ChatToolCallPart>(part).Card),
            part => Assert.Same(second, Assert.IsType<ChatToolCallPart>(part).Card));
    }

    [Fact]
    public void Parts_SeededConstructorText_AppearsAsInitialTextPart()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant, "seed");

        var part = Assert.Single(message.Parts);
        Assert.Equal("seed", Assert.IsType<ChatTextPart>(part).Text);
    }
}
