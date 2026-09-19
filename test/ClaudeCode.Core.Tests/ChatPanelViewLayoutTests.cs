using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// Pins the one layout invariant of the composer toolbar that cannot be expressed in the view model.
/// <para>
/// <c>ChatViewModel.CanConfigure</c> deliberately stays true while a turn is streaming, because
/// switching model or session mode mid-turn is exactly when it matters. WPF propagates
/// <c>IsEnabled="False"</c> to every descendant though, and a child cannot opt back in - so putting
/// the mode/model/Remote Control pills inside the subtree that <c>DraftControlStyle</c> disables on
/// <c>IsBusy</c> silently overrides that binding and locks the pickers for the whole turn. The view
/// model looks correct while the UI is broken, which is why this is asserted against the markup.
/// </para>
/// </summary>
public sealed class ChatPanelViewLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Controls that must stay usable while the agent is working.</summary>
    private static readonly string[] AlwaysEnabled = ["ModeButton", "ModelButton", "RemoteControlButton"];

    [Fact]
    public void ConfigurationPillsAreNotInsideTheSubtreeDisabledWhileTheAgentWorks()
    {
        XDocument view = XDocument.Load(ViewPath());

        XElement[] draftGatedRoots = view
            .Descendants()
            .Where(element => (string?)element.Attribute("Style") == "{StaticResource DraftControlStyle}")
            .ToArray();

        Assert.True(
            draftGatedRoots.Length > 0,
            "No element applies DraftControlStyle any more. That style is what disables draft-only composer "
                + "controls while a turn streams; if it was renamed or removed, retarget this test rather than "
                + "deleting it - it exists to stop the mode/model pickers being swept into that subtree again.");

        foreach (XElement gated in draftGatedRoots)
        {
            string[] trapped = gated
                .DescendantsAndSelf()
                .Select(element => (string?)element.Attribute(X + "Name"))
                .Where(name => name is not null && AlwaysEnabled.Contains(name))
                .Select(name => name!)
                .ToArray();

            Assert.True(
                trapped.Length == 0,
                $"{string.Join(", ", trapped)} sit inside an element styled with DraftControlStyle, which sets "
                    + "IsEnabled=False while IsBusy/IsConfigBusy/IsConnecting. WPF inherits that to every "
                    + "descendant, so their own IsEnabled=\"{Binding CanConfigure}\" cannot re-enable them and "
                    + "the user cannot switch model, session mode or Remote Control while Claude is working. "
                    + "Wrap only the draft-composition buttons instead.");
        }
    }

    [Fact]
    public void ConfigurationPillsStillGateOnCanConfigure()
    {
        XDocument view = XDocument.Load(ViewPath());

        foreach (string name in new[] { "ModeButton", "ModelButton" })
        {
            XElement button = Assert.Single(
                view.Descendants(Xaml + "Button"),
                element => (string?)element.Attribute(X + "Name") == name);


            Assert.Equal("{Binding CanConfigure}", (string?)button.Attribute("IsEnabled"));
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
                "ChatPanelView.xaml"));
}
