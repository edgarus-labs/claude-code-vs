using System.Collections.Generic;
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
    // theme-brushed row (issue #23). What matters for the user is not the shape of the markup but
    // that the default chrome is gone, that the content still has somewhere to render, and that the
    // chosen option is told apart from a merely hovered or focused one - a retemplate whose
    // selection visual is also its hover visual answers nothing.
    [Fact]
    public void OptionRowsTellSelectionApartFromHoverAndFocus()
    {
        XDocument view = XDocument.Load(ViewPath());

        foreach (XElement option in OptionControls(view))
        {
            XElement template = OptionRowTemplate(view, option);

            // No ContentPresenter means the label and description have nowhere to render at all.
            Assert.NotEmpty(template.Descendants(Xaml + "ContentPresenter"));

            HashSet<string> selected = TriggerEffects(template, "IsChecked");
            HashSet<string> hovered = TriggerEffects(template, "IsMouseOver");
            Assert.NotEmpty(TriggerEffects(template, "IsKeyboardFocused"));
            Assert.True(
                selected.Except(hovered).Any(),
                $"The {option.Name.LocalName} row paints nothing when checked that it does not also "
                    + "paint on hover, so the answered option cannot be told from the one under the "
                    + "pointer.");

            // Trigger precedence: a later trigger wins on the same property. An unconditional hover
            // trigger therefore repainted the background of an ALREADY ANSWERED row with the neutral
            // hover tint - it kept its outline and check mark but lost the accent fill, so in a
            // multi-select question the chosen rows looked different depending on where the pointer
            // rested. Hover must be conditioned on the row not being the answer.
            Assert.Empty(UnconditionalHoverTriggers(template));
        }
    }

    private static IEnumerable<XElement> OptionControls(XDocument view) =>
        view.Descendants().Where(
            element => element.Name == Xaml + "RadioButton" || element.Name == Xaml + "CheckBox");

    /// <summary>The ControlTemplate the option's style applies, following BasedOn.</summary>
    private static XElement OptionRowTemplate(XDocument view, XElement option) =>
        Assert.Single(StyleChain(view, option).SelectMany(style => style.Descendants(Xaml + "ControlTemplate")));

    /// <summary>The option's style and everything it derives from, nearest first.</summary>
    private static List<XElement> StyleChain(XDocument view, XElement option)
    {
        var chain = new List<XElement>();
        string? reference = (string?)option.Attribute("Style")
            ?? throw new Xunit.Sdk.XunitException(
                $"The {option.Name.LocalName} option carries no Style, so WPF draws the default "
                    + "radio/checkbox chrome the card is supposed to replace.");

        while (reference is not null)
        {
            Match key = Regex.Match(reference, @"^\{(?:Static|Dynamic)Resource (?<key>[^}\s]+)\}$");
            Assert.True(key.Success, $"Unexpected option style reference: {reference}");
            XElement style = Assert.Single(
                view.Descendants(Xaml + "Style"),
                element => (string?)element.Attribute(
                    XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key")
                    == key.Groups["key"].Value);
            chain.Add(style);
            reference = (string?)style.Attribute("BasedOn");
        }

        return chain;
    }

    /// <summary>What every trigger keyed on <paramref name="property"/> being true paints, as
    /// target/property/value triples. Covers <c>MultiTrigger</c>, since a state that must not fire
    /// unconditionally is expressed as one condition among several.</summary>
    private static HashSet<string> TriggerEffects(XElement template, string property)
    {
        bool FiresOn(XElement trigger) =>
            ((string?)trigger.Attribute("Property") == property && (string?)trigger.Attribute("Value") == "True")
                || trigger.Descendants(Xaml + "Condition").Any(
                    condition => (string?)condition.Attribute("Property") == property
                        && (string?)condition.Attribute("Value") == "True");

        var effects = new HashSet<string>(
            template
                .Descendants()
                .Where(trigger => (trigger.Name == Xaml + "Trigger" || trigger.Name == Xaml + "MultiTrigger") && FiresOn(trigger))
                .SelectMany(trigger => trigger.Elements(Xaml + "Setter"))
                .Select(setter => SetterKey(setter.Attribute("TargetName"), setter.Attribute("Property"), setter.Attribute("Value"))));

        Assert.NotEmpty(effects);
        return effects;
    }

    /// <summary>Every trigger that fires on hover regardless of whether the row is the answer.</summary>
    private static XElement[] UnconditionalHoverTriggers(XElement template) =>
        template
            .Descendants(Xaml + "Trigger")
            .Where(trigger => (string?)trigger.Attribute("Property") == "IsMouseOver"
                && (string?)trigger.Attribute("Value") == "True")
            .ToArray();

    private static string SetterKey(params XAttribute?[] parts) =>
        string.Join("=", parts.Select(part => (string?)part ?? string.Empty));

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

    // A form with three questions rendered all three at once: the bounded scroll region then held a
    // wall of options, and the user had to scroll past questions they had already answered to reach
    // the answer buttons below it. The card steps through the form one question at a time, where a
    // question is the choice field plus the free-text "Other" box Claude sends alongside it - the
    // view model groups those into one step, so the card must render the whole of CurrentStepFields
    // and never the whole Fields collection.
    [Fact]
    public void CardShowsOneQuestionStepAtATimeRatherThanTheWholeForm()
    {
        XDocument view = XDocument.Load(ViewPath());

        XElement? wholeForm = view.Descendants().FirstOrDefault(
            element => IsBindingTo(element.Attribute("ItemsSource"), "Fields"));
        Assert.True(
            wholeForm is null,
            $"<{wholeForm?.Name.LocalName}> binds ItemsSource to the whole Fields collection, so every "
                + "question of the form is on screen at once - the defect: a multi-question form becomes "
                + "a wall of options inside the bounded scroll region and the user scrolls past answered "
                + "questions to reach the buttons.");

        Assert.True(
            view.Descendants().Any(
                element => IsBindingTo(element.Attribute("ItemsSource"), "CurrentStepFields")
                    || IsBindingTo(element.Attribute("Content"), "CurrentStepFields")
                    || IsBindingTo(element.Attribute("DataContext"), "CurrentStepFields")),
            "Nothing in the card binds CurrentStepFields, so the current step has no source. Binding a "
                + "single field instead drops the free-text \"Other\" box that belongs to the question on "
                + "screen - or pages it as a question of its own.");
    }

    /// <summary>Whether the attribute is a binding whose path is exactly <paramref name="path"/>.</summary>
    private static bool IsBindingTo(XAttribute? attribute, string path) =>
        attribute is not null
        && Regex.IsMatch((string)attribute, $@"^\{{Binding (Path=)?{path}[,}}]");

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
