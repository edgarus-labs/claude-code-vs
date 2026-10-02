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
/// Covers the height layout of the elicitation card markup, read as XML: the agent-authored body sits
/// inside a scroll region bounded by the hosting chat panel, and the Decline/Send buttons sit outside
/// it.
/// </summary>
public sealed class ElicitationCardViewLayoutTests
{
    private static readonly XNamespace _xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void AgentAuthoredMessageSitsInsideAScrollRegionBoundedByAShareOfTheChatPanel()
    {
        XDocument view = XDocument.Load(ViewPath());

        XElement message = Assert.Single(
            view.Descendants(_xaml + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding Message}");

        XElement scroll = Assert.Single(
            message.Ancestors(_xaml + "ScrollViewer"),
            element => element.Attribute("MaxHeight") is not null);
        string maxHeight = Regex.Replace((string)scroll.Attribute("MaxHeight")!, @"\s+", " ");

        Assert.Matches(@"^\{Binding (Path=)?ActualHeight,", maxHeight);
        Assert.Matches(
            @"RelativeSource=\{RelativeSource AncestorType=\{x:Type views:ChatPanelView\}\}",
            maxHeight);

        XElement converter = Assert.Single(
            view.Descendants(), element => element.Name.LocalName == "FractionOfConverter");
        var converterKey = (string)converter.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key")!;
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
                view.Descendants(_xaml + "Button"),
                element => (string?)element.Attribute("Command") == command);

            Assert.False(
                button.Ancestors(_xaml + "ScrollViewer").Any(),
                $"The button bound to {command} sits inside the card's ScrollViewer. Bounding the "
                    + "agent-authored body must not sweep the answer buttons in with it: a long form would "
                    + "then scroll them out of view and the user would have to find them to unblock the agent.");
        }
    }

    [Fact]
    public void CardCarriesNoBrushPaletteOfItsOwnSoTheHostThemeReachesIt()
    {
        XDocument view = XDocument.Load(ViewPath());

        Assert.Empty(view.Descendants(_xaml + "ResourceDictionary.MergedDictionaries"));
        Assert.Empty(view.Descendants(_xaml + "SolidColorBrush"));
        Assert.DoesNotContain(
            view.Descendants().SelectMany(element => element.Attributes()),
            attribute => attribute.Value.StartsWith("{StaticResource Chat", System.StringComparison.Ordinal));
    }

    [Fact]
    public void OptionRowsTellSelectionApartFromHoverAndFocus()
    {
        XDocument view = XDocument.Load(ViewPath());

        foreach (XElement option in OptionControls(view))
        {
            XElement template = OptionRowTemplate(view, option);

            Assert.NotEmpty(template.Descendants(_xaml + "ContentPresenter"));

            HashSet<string> selected = TriggerEffects(template, "IsChecked");
            HashSet<string> hovered = TriggerEffects(template, "IsMouseOver");
            Assert.NotEmpty(selected);
            Assert.NotEmpty(TriggerEffects(template, "IsKeyboardFocused"));
            Assert.True(
                selected.Except(hovered).Any(),
                $"The {option.Name.LocalName} row paints nothing when checked that it does not also "
                    + "paint on hover, so the answered option cannot be told from the one under the "
                    + "pointer.");

            Assert.Empty(HoverTriggersThatRepaintAChosenRow(template));
        }
    }

    private static IEnumerable<XElement> OptionControls(XDocument view) =>
        view.Descendants().Where(
            element => element.Name == _xaml + "RadioButton" || element.Name == _xaml + "CheckBox");

    private static XElement OptionRowTemplate(XDocument view, XElement option) =>
        Assert.Single(StyleChain(view, option).SelectMany(style => style.Descendants(_xaml + "ControlTemplate")));

    private static List<XElement> StyleChain(XDocument view, XElement option)
    {
        var chain = new List<XElement>();
        string? reference = (string?)option.Attribute("Style")
            ?? throw new Xunit.Sdk.XunitException(
                $"The {option.Name.LocalName} option carries no Style, so WPF draws the default "
                    + "radio/checkbox chrome the card is supposed to replace.");

        var seen = new HashSet<string>();
        while (reference is not null)
        {
            Match key = Regex.Match(reference, @"^\{(?:Static|Dynamic)Resource (?<key>[^}\s]+)\}$");
            Assert.True(key.Success, $"Unexpected option style reference: {reference}");
            Assert.True(seen.Add(key.Groups["key"].Value), $"Cyclic BasedOn chain at {key.Groups["key"].Value}.");
            XElement style = Assert.Single(
                view.Descendants(_xaml + "Style"),
                element => (string?)element.Attribute(
                    XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key")
                    == key.Groups["key"].Value);
            chain.Add(style);
            reference = (string?)style.Attribute("BasedOn");
        }

        return chain;
    }

    private static HashSet<string> TriggerEffects(XElement template, string property) =>
        new(template
            .Descendants()
            .Where(trigger => (trigger.Name == _xaml + "Trigger" || trigger.Name == _xaml + "MultiTrigger")
                && FiresOn(trigger, property))
            .SelectMany(trigger => trigger.Elements(_xaml + "Setter"))
            .Select(setter => SetterKey(setter.Attribute("TargetName"), setter.Attribute("Property"), setter.Attribute("Value"))));

    private static bool FiresOn(XElement trigger, string property) =>
        ((string?)trigger.Attribute("Property") == property && (string?)trigger.Attribute("Value") == "True")
            || trigger.Descendants(_xaml + "Condition").Any(
                condition => (string?)condition.Attribute("Property") == property
                    && (string?)condition.Attribute("Value") == "True");

    private static XElement[] HoverTriggersThatRepaintAChosenRow(XElement template) =>
        template
            .Descendants()
            .Where(trigger => (trigger.Name == _xaml + "Trigger" || trigger.Name == _xaml + "MultiTrigger")
                && FiresOn(trigger, "IsMouseOver")
                && trigger.Elements(_xaml + "Setter").Any(
                    setter => (string?)setter.Attribute("Property") == "Background")
                && !trigger.Descendants(_xaml + "Condition").Any(
                    condition => (string?)condition.Attribute("Property") == "IsChecked"
                        && (string?)condition.Attribute("Value") == "False"))
            .ToArray();

    private static string SetterKey(params XAttribute?[] parts) =>
        string.Join("=", parts.Select(part => (string?)part ?? string.Empty));

    [Fact]
    public void OptionRowTextScalesWithTheChatFontSize()
    {
        XDocument view = XDocument.Load(ViewPath());

        XElement[] optionTexts = view
            .Descendants()
            .Where(element => element.Name == _xaml + "RadioButton" || element.Name == _xaml + "CheckBox")
            .SelectMany(option => option.Descendants(_xaml + "TextBlock"))
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
