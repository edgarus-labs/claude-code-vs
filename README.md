# Claude Code for Visual Studio

Independent native integration of [Claude Code](https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code)
for Visual Studio 2022/2026, as a real sidebar tool window (WPF, docked like Solution Explorer) — not a
terminal wrapper or a copy of Anthropic's VS Code extension. The agent runs out-of-process as
`claude-agent-acp`, speaking the [Agent Client Protocol](https://agentclientprotocol.com)
(JSON-RPC 2.0 over stdio). The extension streams responses, renders diffs and tool calls, brokers file
reads/writes, and answers permission prompts natively in VS UI. Features depend on the adapter's
advertised capabilities; this is not a claim of feature parity with the official extension.

Authentication reuses the Claude Code CLI's native configuration and credentials, including
`CLAUDE_CONFIG_DIR`. The extension does not read, copy, store, or refresh tokens and does not launch its own
login flow. If needed, run `claude auth login` in a terminal, then use **Check CLI sign-in** in the sidebar.
Restart Visual Studio after changing environment variables.

Install Node.js 22 or newer and the current adapter with
`npm install -g @agentclientprotocol/claude-agent-acp`. The adapter advertises the available models and
model-specific effort levels before the first message. Permission requests remain interactive; choosing
**Yes** approves only that request.

Claude can also *drive Visual Studio itself*: every chat session injects a client-side MCP server
(`ClaudeCode.VsControl.Mcp`) exposing VS automation — open/activate documents, read/replace the selection, run a
build, read the Error List and editor squiggles, run DTE commands, inspect solution structure — as ordinary MCP
tools (see `docs/VsControlProtocol.md` for the exact wire contract).

## Using the sidebar

- **Compose:** Enter sends; Shift+Enter inserts a newline. Normal text paste works as expected.
- **Add document context:** **+** captures the active Visual Studio document, including unsaved edits.
  Switch editor tabs and press **+** again to attach several documents. Adding the same file again
  replaces its stored snapshot instead of duplicating it. Later editor edits do not change an attached
  snapshot; press **+** again on that file to refresh it.
- **Attach images:** use the separate image button or paste an image with Ctrl+V. The **+** button is
  for document context, not image selection.
- **Slash commands:** type `/` to filter the adapter's live command catalog. Use Up/Down to select,
  Tab or Enter to insert the selection without sending, and Escape to dismiss. You can then add
  arguments and send. Skill and plugin commands appear only as advertised by the backend; the sidebar
  does not provide an installed-plugin manager or a plugin reload RPC. Manage installed plugins through
  the Claude Code CLI.
- **Read and copy:** user messages appear in right-aligned bubbles; assistant responses are selectable,
  unboxed Markdown, including tables and code blocks. Both message types have a copy action that copies
  the original message text rather than rendered formatting. Markdown images display their alt text;
  the renderer does not fetch their image URLs.
- **Chat text size:** Ctrl+mouse wheel over the composer or assistant response changes the chat font
  size by one step, from 10 to 28 (default 13). This setting is local to the current chat UI session and
  does not change Visual Studio's editor zoom. Scrolling without Ctrl keeps its normal behavior.

## Installing and debugging

For everyday use, open the built or release `.vsix`, select your regular Visual Studio installation in
the VSIX Installer, close Visual Studio when prompted, and restart it after installation. Building the
project alone does not install an updated extension into your regular instance.

F5 on `ClaudeCode.Vsix` launches a separate **Experimental Instance** for extension debugging. Its
extension installation is separate from regular Visual Studio; testing there does not update the
extension you use in your normal IDE. To use a rebuilt version normally, install the new `.vsix`.

## Solution layout

| Project | TFM | Role |
|---|---|---|
| `src/ClaudeCode.Contracts` | netstandard2.0 | Cross-project interfaces/DTOs (ACP connection, auth, MCP config) — the only thing every other project depends on. |
| `src/ClaudeCode.Acp` | netstandard2.0 | ACP client: JSON-RPC/stdio transport, process spawning, executable resolution. |
| `src/ClaudeCode.Core.ViewModels` | netstandard2.0 | XAML-free chat MVVM (view models, demo/fake connection). |
| `src/ClaudeCode.Core` | net472 + WPF | The sidebar UI (`ChatPanelView` and friends). |
| `src/ClaudeCode.VsControl.Mcp` | net8.0 (exe) | Standalone MCP-over-stdio server; forwards tool calls to VS over a named pipe. |
| `src/ClaudeCode.Vsix` | net48 | The actual VSIX package: `AsyncPackage`, tool window, commands, native CLI authentication status, VS theme bridge, and the named-pipe server VsControl.Mcp talks to. |

## Building

The protocol, view-model, and MCP projects and their tests can be built independently with the .NET SDK.
**Building the full solution and producing or running the `.vsix` require Windows with Visual Studio
2022/2026 and the "Visual Studio extension development" workload installed.** Packaging uses desktop
MSBuild and fails explicitly if the VS SDK targets cannot be loaded. On Windows:

```powershell
dotnet restore ClaudeCodeVS.slnx
msbuild ClaudeCodeVS.slnx -t:Build -p:Configuration=Release
# .vsix lands in src/ClaudeCode.Vsix/bin/Release/net48/
```

The generated `.vsix` is the installer for regular Visual Studio; F5 uses the separate experimental
instance described above.

## CI/CD

- **CI** (`.github/workflows/ci.yml`): every PR (open/update) and push to `develop`/`main` builds the full solution
  in Release and runs all three test projects, on `windows-latest`.
- **CD** (`.github/workflows/cd.yml`): pushing a tag matching `vX.Y.Z` stamps that version into both the
  `.vsixmanifest` `Identity/@Version` (what Visual Studio's Extensions & Updates dialog displays) and the .NET
  assembly metadata (`-p:Version=X.Y.Z`), rebuilds, re-runs tests, and publishes a GitHub Release with the built
  `.vsix` attached.
