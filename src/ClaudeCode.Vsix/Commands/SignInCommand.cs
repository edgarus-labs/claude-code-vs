using System;
using System.Threading.Tasks;
using ClaudeCode.Contracts;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCode.Vsix.Commands
{
    /// <summary>Runs the Claude Code CLI's own browser-based OAuth sign-in flow.</summary>
    [Command(PackageGuids.ClaudeCodeCommandSetString, PackageIds.SignInCommand)]
    internal sealed class SignInCommand : BaseCommand<SignInCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var authService = ClaudeCodeServices.AuthService;
            if (authService == null)
            {
                await VS.MessageBox.ShowErrorAsync("Claude Code", "The extension has not finished initializing yet.");
                return;
            }

            var progress = new Progress<string>(line => VS.StatusBar.ShowMessageAsync($"Claude Code: {line}").FireAndForget());

            await VS.StatusBar.ShowMessageAsync("Claude Code: opening browser sign-in…");
            try
            {
                await authService.SignInAsync(Package.DisposalToken, progress);
                await VS.StatusBar.ShowMessageAsync("Claude Code: signed in.");
            }
            catch (OperationCanceledException)
            {
                await VS.StatusBar.ShowMessageAsync("Claude Code: sign-in cancelled.");
            }
            catch (Exception ex)
            {
                await VS.StatusBar.ShowMessageAsync("Claude Code: sign-in failed.");
                await VS.MessageBox.ShowErrorAsync("Claude Code sign-in failed", ex.Message);
            }
        }

        protected override void BeforeQueryStatus(EventArgs e)
        {
            Command.Enabled = ClaudeCodeServices.AuthService?.CurrentState != AuthState.SigningIn;
        }
    }
}
