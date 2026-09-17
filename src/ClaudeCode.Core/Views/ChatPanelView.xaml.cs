using System;
using System.Windows.Controls;
using ClaudeCode.Core.ViewModels;
using ClaudeCode.Core.ViewModels.Demo;

namespace ClaudeCode.Core.Views
{
    /// <summary>
    /// Root sidebar view. Public parameterless constructor so the Vsix host can instantiate it directly
    /// inside a ToolWindowPane.
    /// </summary>
    public partial class ChatPanelView : UserControl
    {
        /// <summary>
        /// Set by the host (ClaudeCode.Vsix) before the tool window is first created, so this control can be
        /// wired to real ACP/auth services without ClaudeCode.Core ever referencing the host assembly or
        /// ClaudeCode.Acp. Left null (XAML designer, unit tests, standalone preview), the control falls back
        /// to an in-memory demo connection instead of throwing.
        /// </summary>
        public static Func<IChatSessionServices>? ServicesFactory { get; set; }

        private readonly ChatViewModel _viewModel;

        public ChatPanelView()
        {
            InitializeComponent();

            var services = ServicesFactory?.Invoke() ?? new NullChatSessionServices();
            _viewModel = new ChatViewModel(services);
            DataContext = _viewModel;

            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            Unloaded -= OnUnloaded;
            _viewModel.Dispose();
        }
    }
}
