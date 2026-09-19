using ClaudeCode.Acp;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.ComponentModel;
using System.Drawing.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms.Design;

namespace ClaudeCode.Vsix.Options;

[ComVisible(true)]
public sealed class ClaudeCodeOptionsPage : DialogPage
{
    [Category("Claude Code")]
    [DisplayName("ACP executable path")]
    [Description("Optional fully qualified path to the @agentclientprotocol/claude-agent-acp executable, Windows npm shim, or package dist/index.js. Leave blank to find claude-agent-acp on PATH. Requires Node.js 22 or newer for npm installations; the native Claude CLI alone is not an ACP adapter.")]
    [Editor(typeof(FileNameEditor), typeof(UITypeEditor))]
    public string CliExecutablePath { get; set; } = string.Empty;

    [Category("Claude Code")]
    [DisplayName("Notify when Visual Studio is in the background")]
    [Description("Show a Windows notification when Claude finishes a response or needs your permission while another application has focus.")]
    public bool NotifyWhenInBackground { get; set; } = true;

    [Category("Claude Code")]
    [DisplayName("Remote Control at startup")]
    [Description("Turn on Remote Control for every new session so it can be driven from claude.ai/code (same as the CLI's remoteControlAtStartup setting).")]
    public bool RemoteControlAtStartup { get; set; }

    protected override void OnApply(PageApplyEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(CliExecutablePath)
            && (!AcpExecutableResolver.IsFullyQualifiedPath(CliExecutablePath) || !File.Exists(CliExecutablePath)))
        {
            VsShellUtilities.ShowMessageBox(
                this.Site,
                $"The ACP executable path '{CliExecutablePath}' must be fully qualified and refer to an existing file. Correct it, or leave it blank to find claude-agent-acp on PATH.",
                "Claude Code",
                OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            e.ApplyBehavior = ApplyKind.CancelNoNavigate;
            return;
        }

        base.OnApply(e);
    }
}
