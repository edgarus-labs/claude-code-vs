using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// Pins the height invariant of the elicitation card, which no view model can express.
/// <para>
/// Every string on this card is agent-authored and <c>ElicitationRequestViewModel.MaxDisplayTextLength</c>
/// deliberately admits a 4 000-character message. <c>ChatPanelView.xaml</c> hosts the card in an
/// <c>Auto</c> grid row whose only <c>*</c> sibling is the transcript, so whatever the card does not
/// height-bound is granted its full desired height: WPF then collapses the transcript to zero and
/// arranges the composer - and the card's own Decline/Send buttons - past the bottom of the tool
/// window, where they are clipped. The user can neither answer nor decline the form and the agent's
/// <c>elicitation/create</c> stays blocked. The agent-authored body must therefore sit inside the
/// bounded scroll region, and the answer buttons must stay outside it so they are never scrolled
/// out of reach.
/// </para>
/// </summary>
public sealed class ElicitationCardViewLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void AgentAuthoredMessageSitsInsideAHeightBoundedScrollRegion()
    {
        XDocument view = XDocument.Load(ViewPath());

        XElement message = Assert.Single(
            view.Descendants(Xaml + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding Message}");

        Assert.True(
            message.Ancestors(Xaml + "ScrollViewer").Any(scroll => scroll.Attribute("MaxHeight") is not null),
            "The agent-authored Message TextBlock is not inside a ScrollViewer that caps its height. A "
                + "4 000-character message - a length the view model explicitly admits - then makes the card "
                + "taller than the tool window, and because the card sits in an Auto grid row WPF pushes the "
                + "card's own Decline/Send buttons and the composer off the bottom edge, leaving the form "
                + "unanswerable. Put the message in the same bounded scroll region as the fields.");
    }

    [Fact]
    public void AnswerButtonsStayOutsideTheScrollRegion()
    {
        XDocument view = XDocument.Load(ViewPath());

        foreach (string command in new[] { "{Binding DeclineCommand}", "{Binding SubmitCommand}" })
        {
            XElement button = Assert.Single(
                view.Descendants(Xaml + "Button"),
                element => (string?)element.Attribute("Command") == command);

            Assert.False(
                button.Ancestors(Xaml + "ScrollViewer").Any(),
                $"The button bound to {command} sits inside the card's ScrollViewer. Bounding the "
                    + "agent-authored body must not sweep the answer buttons in with it: a long form would "
                    + "then scroll them out of view and the user would have to find them to unblock the agent.");
        }
    }

    private static string ViewPath([CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(testFilePath)!,
                "..",
                "..",
                "src",
                "ClaudeCode.Core",
                "Views",
                "ElicitationCardView.xaml"));
}
