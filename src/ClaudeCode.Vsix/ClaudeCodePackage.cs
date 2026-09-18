using ClaudeCode.Vsix.Auth;
using ClaudeCode.Vsix.Connections;
using ClaudeCode.Vsix.Options;
using ClaudeCode.Vsix.VsControl;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.Vsix;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideBindingPath]
[InstalledProductRegistration("Claude Code for Visual Studio", "Chat with Claude Code from a native sidebar tool window, backed by the Agent Client Protocol.", GeneratedProductVersion.Value)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(ChatToolWindowPane), Style = VsDockStyle.Tabbed, Window = Microsoft.VisualStudio.Shell.Interop.ToolWindowGuids80.SolutionExplorer)]
[ProvideOptionPage(typeof(ClaudeCodeOptionsPage), "Claude Code", "General", 0, 0, true)]
[Guid(PackageGuids.ClaudeCodePackageString)]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "AsyncPackage uses the Visual Studio-managed Dispose(bool) lifecycle rather than " +
    "implementing IDisposable. This override disposes its owned fields; the session registry is " +
    "disposed in finally even if earlier UI-thread cleanup throws.")]
public sealed class ClaudeCodePackage : AsyncPackage
{
    private AcpAuthService? _authService;
    private VsControlSessionRegistry? _vsControlSessionRegistry;
    private ActiveEditorDocumentTracker? _editorDocumentTracker;
    private SolutionEvents? _solutionEvents;
    private string? _cachedWorkspaceRoot;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var editorDocumentTracker = new ActiveEditorDocumentTracker();
        _editorDocumentTracker = editorDocumentTracker;
        await editorDocumentTracker.InitializeAsync(cancellationToken);

        _authService = new AcpAuthService(() => GetOptions().CliExecutablePath);
        _vsControlSessionRegistry = new VsControlSessionRegistry();

        // Seed the workspace-root cache once on the UI thread, then keep it current via solution
        // events instead of blocking every GetWorkspaceRoot() call on JoinableTaskFactory.Run.
        _solutionEvents = VS.Events.SolutionEvents;
        _solutionEvents.OnAfterOpenSolution += OnSolutionOpened;
        _solutionEvents.OnAfterCloseSolution += OnSolutionClosed;
        var currentSolution = await VS.Solutions.GetCurrentSolutionAsync();
        _cachedWorkspaceRoot = ComputeWorkspaceRoot(currentSolution?.FullPath);

        var connectionFactory = new ClaudeCodeConnectionFactory(GetOptions, GetWorkspaceRoot, _vsControlSessionRegistry);

        ClaudeCodeServices.ConnectionFactory = connectionFactory;
        ClaudeCodeServices.AuthService = _authService;
        ClaudeCodeServices.GetWorkspaceRoot = GetWorkspaceRoot;

        ClaudeCode.Core.Views.ChatPanelView.ServicesFactory =
            () => new VsChatSessionServices(ClaudeCodeServices.ConnectionFactory!, ClaudeCodeServices.AuthService!, ClaudeCodeServices.GetWorkspaceRoot!, editorDocumentTracker);

        await this.RegisterCommandsAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                JoinableTaskFactory.Run(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    _editorDocumentTracker?.Dispose();
                    if (_solutionEvents is not null)
                    {
                        _solutionEvents.OnAfterOpenSolution -= OnSolutionOpened;
                        _solutionEvents.OnAfterCloseSolution -= OnSolutionClosed;
                    }
                });
            }
            finally
            {
                // Must run even if the UI-thread cleanup above throws, or the registry and the
                // extension-scoped globals below would leak/outlive this package instance.
                _vsControlSessionRegistry?.Dispose();

                ClaudeCodeServices.ConnectionFactory = null;
                ClaudeCodeServices.AuthService = null;
                ClaudeCodeServices.GetWorkspaceRoot = null;
                ClaudeCode.Core.Views.ChatPanelView.ServicesFactory = null;
            }
        }

        base.Dispose(disposing);
    }

    private ClaudeCodeOptionsPage GetOptions()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return (ClaudeCodeOptionsPage)GetDialogPage(typeof(ClaudeCodeOptionsPage));
    }

    private string? GetWorkspaceRoot() => _cachedWorkspaceRoot;

    private void OnSolutionOpened(Solution? solution)
    {
        _cachedWorkspaceRoot = ComputeWorkspaceRoot(solution?.FullPath);
    }

    private void OnSolutionClosed()
    {
        _cachedWorkspaceRoot = null;
    }

    /// Pure so it can be exercised without a live VS host; not itself VS-SDK dependent.
    internal static string? ComputeWorkspaceRoot(string? solutionFullPath) =>
        solutionFullPath is string path ? System.IO.Path.GetDirectoryName(path) : null;
}
