using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Commands;

[Command(PackageGuids.ClaudeCodeCommandSetString, PackageIds.OpenChatWindowCommand)]
internal sealed class OpenChatWindowCommand : BaseCommand<OpenChatWindowCommand>
{
    /// <summary>
    /// Asynchronously locates and displays the Chat tool window pane when the command is executed.
    /// </summary>
    /// <param name="e">The e.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var window = await Package.FindToolWindowAsync(typeof(ChatToolWindowPane), 0, create: true, Package.DisposalToken);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(Package.DisposalToken);
        if (window?.Frame is IVsWindowFrame frame)
        {
            ErrorHandler.ThrowOnFailure(frame.Show());
        }
    }
}
