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
/// <para>
/// TDD exception, stated deliberately: this test project targets net10.0 without WPF, so the view
/// cannot be instantiated and its IsEnabled inheritance observed for real. These facts read the
/// markup as XML and locate the gated subtree by the literal <c>Style="{StaticResource DraftControlStyle}"</c>
/// attribute, so they fail on a behaviour-preserving rewrite of that markup (a property-element
/// style, a BasedOn style) and would pass on the same hazard expressed differently. They are kept as
/// a ratchet against the exact regression that shipped, which is the only form of coverage this
/// invariant can have here; retarget them rather than deleting them when the markup shape changes.
/// </para>
/// </summary>
public sealed class ChatPanelViewLayoutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Controls that must stay usable while the agent is working.</summary>
    private static readonly string[] AlwaysEnabled = ["ModeButton", "ModelButton", "RemoteControlButton"];

    /// <summary>Draft-composition controls that must stay disabled while the agent is working.</summary>
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

    // The counterpart ratchet. Without it, lifting AttachButton out of the wrapper - re-enabling
    // "attach the active document while a turn is in flight", the exact hazard the gate exists for
    // - passes the test above, the analyzers and the build. Failing this list is also how a rename
    // or removal of DraftControlStyle shows up here: the gated set goes empty.
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

    // Matches only the literal Style="{StaticResource DraftControlStyle}" attribute form: the style
    // applied through a <X.Style> property element, or inherited from a parent Setter, would slip
    // past. Acceptable because every caller asserts the returned set is non-empty, so the day the
    // markup stops using this form the tests fail rather than silently passing on nothing.
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
