using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Commands;

[Command(PackageGuids.ClaudeCodeCommandSetString, PackageIds.SignOutCommand)]
internal sealed class SignOutCommand : BaseCommand<SignOutCommand>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var authService = ClaudeCodeServices.AuthService;
        if (authService is null)
        {
            return;
        }

        await authService.SignOutAsync(Package.DisposalToken);
        await VS.StatusBar.ShowMessageAsync("Claude Code: sign-in state cleared (native credentials unchanged).");
    }
}
