using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace ClaudeCode.Core.Tests;

/// <summary>
/// Covers the composer toolbar markup of <c>ChatPanelView.xaml</c>, read as XML: the mode, model and
/// Remote Control pills sit outside the subtree that <c>DraftControlStyle</c> disables on
/// <c>IsBusy</c>.
/// </summary>
public sealed class ChatPanelViewLayoutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] AlwaysEnabled = ["ModeButton", "ModelButton", "RemoteControlButton"];

    private static readonly string[] AlwaysGated = ["AttachButton"];

    [Fact]
    public void ConfigurationPillsAreNotInsideTheSubtreeDisabledWhileTheAgentWorks()
    {
        XElement[] draftGatedRoots = DraftGatedRoots(XDocument.Load(ViewPath()));

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
    public void DraftCompositionControlsStayInsideTheSubtreeDisabledWhileTheAgentWorks()
    {
        string[] gated = DraftGatedRoots(XDocument.Load(ViewPath()))
            .SelectMany(root => root.Descendants())
            .Select(element => (string?)element.Attribute(X + "Name"))
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

        foreach (string name in AlwaysGated)
        {
            Assert.True(
                gated.Contains(name),
                $"{name} is no longer inside an element styled with DraftControlStyle, so it stays clickable "
                    + "while a turn is streaming. Attaching the active document mid-turn is the hazard that "
                    + "gate exists for. Put it back inside the wrapper rather than relaxing this test.");
        }
    }

    private static XElement[] DraftGatedRoots(XDocument view) => view
        .Descendants()
        .Where(element => (string?)element.Attribute("Style") == "{StaticResource DraftControlStyle}")
        .ToArray();

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
