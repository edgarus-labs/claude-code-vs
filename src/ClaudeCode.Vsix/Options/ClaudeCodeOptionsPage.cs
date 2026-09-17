using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.Drawing.Design;
using System.Runtime.InteropServices;
using System.Windows.Forms.Design;

namespace ClaudeCode.Vsix.Options;

[ComVisible(true)]
public sealed class ClaudeCodeOptionsPage : DialogPage
{
    [Category("Claude Code")]
    [DisplayName("ACP executable path")]
    [Description("Optional path to the @agentclientprotocol/claude-agent-acp executable, Windows npm shim, or package dist/index.js. Leave blank to find claude-agent-acp on PATH. Requires Node.js 22 or newer for npm installations; the native Claude CLI alone is not an ACP adapter.")]
    [Editor(typeof(FileNameEditor), typeof(UITypeEditor))]
    public string CliExecutablePath { get; set; } = string.Empty;
}
