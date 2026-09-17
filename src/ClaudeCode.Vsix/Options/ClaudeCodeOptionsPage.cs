using System.ComponentModel;
using System.Drawing.Design;
using System.Runtime.InteropServices;
using System.Windows.Forms.Design;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCode.Vsix.Options
{
    /// <summary>
    /// Tools &gt; Options &gt; Claude Code &gt; General page. Deliberately limited to plain settings that the
    /// stock <see cref="DialogPage"/> PropertyGrid renders well out of the box (a browsable file path and a
    /// free-text model override) - sign in/out is a stateful, potentially long-running operation with
    /// progress feedback, which does not fit a PropertyGrid row, so it is exposed instead as top-level VS
    /// commands (see <see cref="Commands.SignInCommand"/>/<see cref="Commands.SignOutCommand"/>) and read
    /// from the chat tool window itself, rather than as buttons hosted in this page via a
    /// DialogPage.Window/ElementHost override.
    /// </summary>
    [ComVisible(true)]
    public class ClaudeCodeOptionsPage : DialogPage
    {
        [Category("Claude Code")]
        [DisplayName("CLI executable path")]
        [Description("Optional override for the claude-code-acp (or claude) executable. Leave blank to auto-detect it on PATH.")]
        [Editor(typeof(FileNameEditor), typeof(UITypeEditor))]
        public string CliExecutablePath { get; set; } = string.Empty;

        [Category("Claude Code")]
        [DisplayName("Default model")]
        [Description("Optional model identifier passed to the agent for new sessions. Leave blank to use the CLI's own default.")]
        public string DefaultModel { get; set; } = string.Empty;
    }
}
