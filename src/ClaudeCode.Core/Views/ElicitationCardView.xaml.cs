using ClaudeCode.Core.ViewModels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ClaudeCode.Core.Views;

/// <summary>Renders the pending <c>elicitation/create</c> form (AskUserQuestion) so the user can
/// actually answer it. The DataContext is an <see cref="ElicitationRequestViewModel"/> and every
/// affordance is bound to its commands, so there is no code-behind state to keep in sync; the only
/// code here announces a newly arrived form to assistive technology, which XAML cannot do.</summary>
public partial class ElicitationCardView : UserControl
{
    public ElicitationCardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Announces a newly arrived form through UI Automation. WPF surfaces
    /// <c>AutomationProperties.LiveSetting</c> as a UIA property but never raises
    /// <c>LiveRegionChanged</c> itself, so the assertive live region on the message announces nothing
    /// unless the application raises the event - and this card is a blocking prompt: the agent's turn
    /// does not continue until it is answered or declined, so a screen-reader user has to be told it
    /// appeared. The card stays in the visual tree and is toggled by Visibility, so a new form
    /// arrives as a DataContext change to a non-null view model.</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not ElicitationRequestViewModel
            || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        AutomationPeer? peer = UIElementAutomationPeer.FromElement(MessageText)
            ?? UIElementAutomationPeer.CreatePeerForElement(MessageText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
