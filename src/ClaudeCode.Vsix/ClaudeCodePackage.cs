using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCode.Vsix.Auth;
using ClaudeCode.Vsix.Connections;
using ClaudeCode.Vsix.Options;
using ClaudeCode.Vsix.VsControl;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCode.Vsix
{
    /// <summary>
    /// Root VSPackage. Registers the Claude Code chat tool window, the Tools &gt; Options page, and the
    /// View-menu/toolbar commands, then constructs the process-lifetime services (auth, connection factory,
    /// VS-control pipe registry) that everything else in this assembly - and, through
    /// <see cref="VsChatSessionServices"/>, ClaudeCode.Core's chat view model - reads from
    /// <see cref="ClaudeCodeServices"/>.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Claude Code for Visual Studio", "Chat with Claude Code from a native sidebar tool window, backed by the Agent Client Protocol.", "0.1")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ChatToolWindowPane), Style = VsDockStyle.Tabbed, Window = Microsoft.VisualStudio.Shell.Interop.ToolWindowGuids80.SolutionExplorer)]
    [ProvideOptionPage(typeof(ClaudeCodeOptionsPage), "Claude Code", "General", 0, 0, true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuids.ClaudeCodePackageString)]
    public sealed class ClaudeCodePackage : AsyncPackage
    {
        private AcpAuthService? _authService;
        private VsControlSessionRegistry? _vsControlSessionRegistry;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _authService = new AcpAuthService(() => CliLocator.ResolveClaudeCliForAuth(GetOptions().CliExecutablePath));
            _vsControlSessionRegistry = new VsControlSessionRegistry();

            var connectionFactory = new ClaudeCodeConnectionFactory(GetOptions, _authService, GetWorkspaceRoot, _vsControlSessionRegistry);

            ClaudeCodeServices.ConnectionFactory = connectionFactory;
            ClaudeCodeServices.AuthService = _authService;
            ClaudeCodeServices.GetWorkspaceRoot = GetWorkspaceRoot;

            ClaudeCode.Core.Views.ChatPanelView.ServicesFactory =
                () => new VsChatSessionServices(ClaudeCodeServices.ConnectionFactory!, ClaudeCodeServices.AuthService!, ClaudeCodeServices.GetWorkspaceRoot!);

            // Best-effort initial sign-in status refresh; failures here must never fault package load.
            try
            {
                await _authService.IsSignedInAsync(cancellationToken);
            }
            catch (Exception)
            {
                // Corrupt/unreadable credential file - treated as signed out; the user can sign in again.
            }

            await this.RegisterCommandsAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _vsControlSessionRegistry?.Dispose();
            }

            base.Dispose(disposing);
        }

        private ClaudeCodeOptionsPage GetOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return (ClaudeCodeOptionsPage)GetDialogPage(typeof(ClaudeCodeOptionsPage));
        }

        private string? GetWorkspaceRoot()
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                return solution?.FullPath is string path ? System.IO.Path.GetDirectoryName(path) : null;
            });
        }
    }
}
