using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCode.Vsix
{
    /// <summary>
    /// The sidebar chat tool window. Hosts a plain <see cref="ContentControl"/> wrapper around
    /// <see cref="ClaudeCode.Core.Views.ChatPanelView"/> so the real content can be swapped later (e.g. if
    /// the view ever needs to be recreated) without recreating the pane itself.
    /// </summary>
    [Guid(PackageGuids.ChatToolWindowPersistanceString)]
    public class ChatToolWindowPane : ToolWindowPane
    {
        public ChatToolWindowPane() : base(null)
        {
            Caption = "Claude Code";
            BitmapImageMoniker = KnownMonikers.CommentSparkle;

            var host = new ContentControl
            {
                Content = new ClaudeCode.Core.Views.ChatPanelView(),
            };
            Content = host;
        }
    }
}
