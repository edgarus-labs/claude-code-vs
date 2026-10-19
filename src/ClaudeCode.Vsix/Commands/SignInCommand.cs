using ClaudeCode.Contracts;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix.Commands;

/// <summary>
/// Represents a command to authenticate a user with the provided credentials.
/// </summary>
[Command(PackageGuids.ClaudeCodeCommandSetString, PackageIds.SignInCommand)]
internal sealed class SignInCommand : BaseCommand<SignInCommand>
{
    /// <summary>
    /// Asynchronously executes the CLI sign-in process, displaying progress and handling success, cancellation, or failure scenarios.
    /// </summary>
    /// <param name="e">The e.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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

    /// <summary>
    /// Enables or disables the command based on whether the authentication service is currently signing in.
    /// </summary>
    /// <param name="e">The e.</param>
    protected override void BeforeQueryStatus(EventArgs e)
    {
        Command.Enabled = ClaudeCodeServices.AuthService?.CurrentState != AuthState.SigningIn;
    }
}
