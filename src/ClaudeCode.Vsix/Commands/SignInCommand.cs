using ClaudeCode.Contracts;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Commands;

[Command(PackageGuids.ClaudeCodeCommandSetString, PackageIds.SignInCommand)]
internal sealed class SignInCommand : BaseCommand<SignInCommand>
{
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var authService = ClaudeCodeServices.AuthService;
        if (authService is null)
        {
            await VS.MessageBox.ShowErrorAsync("Claude Code", "The extension has not finished initializing yet.");

            return;
        }

        var progress = new Progress<string>(line => VS.StatusBar.ShowMessageAsync($"Claude Code: {line}").FireAndForget());

        await VS.StatusBar.ShowMessageAsync("Claude Code: checking CLI sign-in…");
        try
        {
            await authService.SignInAsync(Package.DisposalToken, progress);
            await VS.StatusBar.ShowMessageAsync("Claude Code: CLI sign-in verified.");
        }
        catch (OperationCanceledException)
        {
            await VS.StatusBar.ShowMessageAsync("Claude Code: sign-in check cancelled.");
        }
        catch (Exception ex)
        {
            await VS.StatusBar.ShowMessageAsync("Claude Code: sign-in check failed.");
            await VS.MessageBox.ShowErrorAsync("Claude Code sign-in check failed", ex.Message);
        }
    }

    protected override void BeforeQueryStatus(EventArgs e)
    {
        Command.Enabled = ClaudeCodeServices.AuthService?.CurrentState != AuthState.SigningIn;
    }
}
