using System.Windows.Controls;

namespace ClaudeCode.Core.Views;

/// <summary>Renders the pending <c>elicitation/create</c> form (AskUserQuestion) so the user can
/// actually answer it. Purely declarative: the DataContext is an
/// <see cref="ViewModels.ElicitationRequestViewModel"/> and every affordance is bound to its
/// commands, so there is no code-behind state to keep in sync.</summary>
public partial class ElicitationCardView : UserControl
{
    public ElicitationCardView()
    {
        InitializeComponent();
    }
}
