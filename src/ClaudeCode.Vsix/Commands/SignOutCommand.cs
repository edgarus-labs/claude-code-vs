using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCode.Vsix.Commands
{
    /// <summary>Signs out of Claude Code and removes the locally stored OAuth credential.</summary>
    [Command(PackageGuids.ClaudeCodeCommandSetString, PackageIds.SignOutCommand)]
    internal sealed class SignOutCommand : BaseCommand<SignOutCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var authService = ClaudeCodeServices.AuthService;
            if (authService == null)
            {
                return;
            }

            await authService.SignOutAsync(Package.DisposalToken);
            await VS.StatusBar.ShowMessageAsync("Claude Code: signed out.");
        }
    }
}
