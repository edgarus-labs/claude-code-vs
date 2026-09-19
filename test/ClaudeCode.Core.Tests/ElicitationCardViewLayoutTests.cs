using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
/// <c>elicitation/create</c> stays blocked. The agent-authored body must therefore sit inside a
/// scroll region bounded by the hosting chat panel, and the answer buttons must stay outside it so
/// they are never scrolled out of reach.
/// </para>
/// <para>
/// TDD exception, stated deliberately: this test project targets net10.0 without WPF, so the card
/// cannot be measured and arranged for real. These facts read the markup as XML instead, which is
/// the only way the layout contract can be pinned at all; they are a ratchet against the exact
/// regressions that were shipped, not a substitute for a layout test.
/// </para>
/// </summary>
public sealed class ElicitationCardViewLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void AgentAuthoredMessageSitsInsideAScrollRegionBoundedByAShareOfTheChatPanel()
    {
        XDocument view = XDocument.Load(ViewPath());

        XElement message = Assert.Single(
            view.Descendants(Xaml + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding Message}");

        XElement scroll = Assert.Single(
            message.Ancestors(Xaml + "ScrollViewer"),
            element => element.Attribute("MaxHeight") is not null);
        string maxHeight = Regex.Replace((string)scroll.Attribute("MaxHeight")!, @"\s+", " ");

        // The bound must come from the chat panel that hosts the card. Binding to the nearest Window
        // looked right in a floating pane and was wrong everywhere else: docked, the nearest Window
        // is the whole IDE, so a long form got a scroll region taller than the tool window and the
        // Decline/Send buttons were pushed off the bottom edge - the defect this file exists for.
        Assert.Matches(@"^\{Binding (Path=)?ActualHeight,", maxHeight);
        Assert.Matches(
            @"RelativeSource=\{RelativeSource AncestorType=\{x:Type views:ChatPanelView\}\}",
            maxHeight);

        XElement converter = Assert.Single(
            view.Descendants(), element => element.Name.LocalName == "FractionOfConverter");
        string converterKey = (string)converter.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key")!;
        Assert.Contains($"Converter={{StaticResource {converterKey}}}", maxHeight);

        Match fraction = Regex.Match(maxHeight, @"ConverterParameter=([0-9.]+)");
        Assert.True(fraction.Success, "The MaxHeight binding carries no ConverterParameter, so FractionOfConverter yields no bound at all.");
        double share = double.Parse(fraction.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(share, 0.1, 0.9);
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

    // WPF resolves a DynamicResource from the element's own dictionary first and only then walks up
    // to the parent, so a card that merges the standalone dark palette into its own Resources never
    // sees the VS-themed brushes the host writes onto ChatPanelView.Resources: under the Light and
    // Blue themes it painted a dark box with light text inside a light tool window.
    [Fact]
    public void CardCarriesNoBrushPaletteOfItsOwnSoTheHostThemeReachesIt()
    {
        XDocument view = XDocument.Load(ViewPath());

        Assert.Empty(view.Descendants(Xaml + "ResourceDictionary.MergedDictionaries"));
        Assert.Empty(view.Descendants(Xaml + "SolidColorBrush"));
        Assert.DoesNotContain(
            view.Descendants().SelectMany(element => element.Attributes()),
            attribute => attribute.Value.StartsWith("{StaticResource Chat", System.StringComparison.Ordinal));
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
