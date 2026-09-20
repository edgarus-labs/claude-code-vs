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

    // The options were drawn with the stock WPF RadioButton/CheckBox chrome: a system-sized circle
    // or box next to chat-styled text, inside a card whose every other surface is a rounded,
    // theme-brushed row (issue #23). The replacement templates the whole row like the model and mode
    // pickers in ChatPanelView. RadioButton and CheckBox are both ToggleButtons, so one row style
    // serves single- and multi-select; these facts pin that the default chrome is gone, that the row
    // is the control surface, and that checked, hover and keyboard-focus states each still have a
    // visual of their own.
    [Fact]
    public void OptionsAreChatStyledRowsInsteadOfDefaultToggleChrome()
    {
        XDocument view = XDocument.Load(ViewPath());
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement[] options = view
            .Descendants()
            .Where(element => element.Name == Xaml + "RadioButton" || element.Name == Xaml + "CheckBox")
            .ToArray();
        Assert.Equal(2, options.Length);

        foreach (XElement option in options)
        {
            string style = (string?)option.Attribute("Style")
                ?? throw new Xunit.Sdk.XunitException(
                    $"The {option.Name.LocalName} option carries no Style, so WPF draws the default "
                        + "radio/checkbox chrome the card is supposed to replace.");
            Match key = Regex.Match(style, @"^\{StaticResource (?<key>[A-Za-z0-9_]+)\}$");
            Assert.True(key.Success, $"Unexpected option style reference: {style}");

            XElement rowStyle = Assert.Single(
                view.Descendants(Xaml + "Style"),
                element => (string?)element.Attribute(x + "Key") == key.Groups["key"].Value);
            Assert.Equal("ToggleButton", (string?)rowStyle.Attribute("TargetType"));
            Assert.Contains(
                rowStyle.Elements(Xaml + "Setter"),
                setter => (string?)setter.Attribute("Property") == "HorizontalContentAlignment"
                    && (string?)setter.Attribute("Value") == "Stretch");

            XElement template = Assert.Single(rowStyle.Descendants(Xaml + "ControlTemplate"));

            // The label, the description and the selection glyph all sit inside one templated
            // border: that border is what the user clicks and what paints every state, so the hit
            // target is the row rather than a glyph beside it.
            XElement content = Assert.Single(template.Descendants(Xaml + "ContentPresenter"));
            Assert.Contains(content.Ancestors(Xaml + "Border"), border => border.Parent == template);

            foreach (string state in new[] { "IsChecked", "IsMouseOver", "IsKeyboardFocused" })
            {
                XElement trigger = Assert.Single(
                    template.Descendants(Xaml + "Trigger"),
                    element => (string?)element.Attribute("Property") == state);
                Assert.NotEmpty(trigger.Elements(Xaml + "Setter"));
            }
        }
    }

    // Ctrl+wheel scaling drives ChatPanelView.ChatTextFontSize; a row that hard-codes a size stays
    // put while the text around it grows, which is the scaling half of issue #23.
    [Fact]
    public void OptionRowTextScalesWithTheChatFontSize()
    {
        XDocument view = XDocument.Load(ViewPath());

        XElement[] optionTexts = view
            .Descendants()
            .Where(element => element.Name == Xaml + "RadioButton" || element.Name == Xaml + "CheckBox")
            .SelectMany(option => option.Descendants(Xaml + "TextBlock"))
            .ToArray();
        Assert.NotEmpty(optionTexts);

        foreach (XElement text in optionTexts)
        {
            string fontSize = (string?)text.Attribute("FontSize")
                ?? throw new Xunit.Sdk.XunitException(
                    $"Option text {(string?)text.Attribute("Text")} has no FontSize, so it keeps the "
                        + "inherited default and ignores Ctrl+wheel scaling.");
            Assert.Contains("ChatTextFontSize", fontSize);
            Assert.Contains("ChatTextFontSizeConverter", fontSize);
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
