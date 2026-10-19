using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Commands;

/// <summary>
/// Represents a command to sign out the current user from the system.
/// </summary>
[Command(PackageGuids.ClaudeCodeCommandSetString, PackageIds.SignOutCommand)]
internal sealed class SignOutCommand : BaseCommand<SignOutCommand>
{
    /// <summary>
    /// Asynchronously signs out the user and displays a status bar message indicating the sign-out state was cleared.
    /// </summary>
    /// <param name="e">The e.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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
