using ClaudeCode.Core.ViewModels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ClaudeCode.Core.Views;

/// <summary>Renders the pending <c>elicitation/create</c> form (AskUserQuestion) bound to an
/// <see cref="ElicitationRequestViewModel"/>, and announces a newly arrived form to assistive technology.</summary>
public partial class ElicitationCardView : UserControl
{
    public ElicitationCardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

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
