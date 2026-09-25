# Claude Code for Visual Studio

**A native Claude Code sidebar for Visual Studio 2022/2026** — a real docked tool window (WPF, sits
next to Solution Explorer), not a terminal wrapper and not a port of Anthropic's VS Code extension.
Chat, review diffs, approve plans, and let Claude build, debug and drive the IDE itself, all without
leaving Visual Studio.

[![CI](https://github.com/edgarus-labs/claude-code-vs/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/edgarus-labs/claude-code-vs/actions/workflows/ci.yml)
[![CD](https://github.com/edgarus-labs/claude-code-vs/actions/workflows/cd.yml/badge.svg)](https://github.com/edgarus-labs/claude-code-vs/actions/workflows/cd.yml)
[![Latest release](https://img.shields.io/github/v/release/edgarus-labs/claude-code-vs?label=release)](https://github.com/edgarus-labs/claude-code-vs/releases/latest)
[![Visual Studio](https://img.shields.io/badge/Visual%20Studio-2022%20%7C%202026-5C2D91?logo=visualstudio&logoColor=white)](https://visualstudio.microsoft.com/)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Agent Client Protocol](https://img.shields.io/badge/protocol-ACP-blue)](https://agentclientprotocol.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)

## Why this exists

Anthropic ships an official Claude Code extension for VS Code, but not for Visual Studio. This project
fills that gap with a first-class WPF sidebar built around the same open
[Agent Client Protocol](https://agentclientprotocol.com) (JSON-RPC 2.0 over stdio) the official
extensions use, rather than shelling out to a terminal. The agent (`claude-agent-acp`) runs
out-of-process; the extension streams its responses, renders diffs and tool calls, brokers file
reads/writes, and answers permission prompts natively in the Visual Studio UI. Features depend on the
adapter's advertised capabilities — this is not a claim of feature parity with the official extension.

## Features at a glance

- **Native chat sidebar** — streaming Markdown, live diffs, session history, and Manual / Accept Edits
  / Plan / Auto modes.
- **Plan mode** — review an Implementation Plan as a document tab, approve it or send comments back
  before Claude implements anything.
- **Changed Files tracking** — per-file or bulk accept/reject for everything Claude edits in a session.
- **Visual Studio automation** — Claude can open/activate documents, add files to projects, and build,
  rebuild or clean the solution or a single project.
- **Interactive debugging** — breakpoints, stepping, call stack, locals, expression evaluation, and UI
  Automation of the running app (click buttons, type into fields, screenshot windows).
- **Remote Control** — drive the same session from [claude.ai/code](https://claude.ai/code).
- **Reuses your existing Claude Code login** — `/login` and `/logout` in the sidebar delegate to the
  adapter's own bundled CLI; the extension never reads or stores credentials itself.

## Table of contents

- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Using the sidebar](#using-the-sidebar)
- [Installing and debugging](#installing-and-debugging)
- [Solution layout](#solution-layout)
- [Building](#building)
- [CI/CD](#cicd)
- [Security](#security)
- [Contributing](#contributing)
- [License](#license)

## Requirements

- Visual Studio 2022 or 2026, with the **.NET 8.0 Runtime (Long Term Support)** individual component
  installed (see [Installing and debugging](#installing-and-debugging)).
- [Node.js](https://nodejs.org) 22 or newer.
- The [Claude Code CLI](https://docs.claude.com/en/docs/claude-code) signed in (`claude auth login`).

## Getting started

Install the adapter Claude Code speaks to over ACP:

```powershell
npm install -g @agentclientprotocol/claude-agent-acp
```

Authentication reuses the Claude Code CLI's native configuration and credentials, including
`CLAUDE_CONFIG_DIR`. Type `/login` in the sidebar to sign in: it opens a visible console running the
adapter's own bundled CLI (`claude-agent-acp --cli auth login --claudeai`), which handles the OAuth
flow in your browser; `/logout` runs the same CLI's logout in the background, signing out
everywhere on this machine (not only Visual Studio), after a confirmation. The extension itself
never reads or stores credentials — only the process exit code and the existing status probe cross
back (a failed logout's stderr tail goes to the Visual Studio ActivityLog, never the UI). The one component that reads an OAuth
token directly (the usage-limit lookup) does so in a short-lived subprocess, and only the trimmed
JSON it prints crosses back. You can also run `claude auth login` in a terminal instead, then use
**Check CLI sign-in** in the sidebar. Restart Visual Studio after changing environment variables.

The adapter advertises the available models and model-specific effort levels before the first message.
The extension has no model list of its own: a new model shows up in the model picker once the adapter
supports it. Claude Opus 5.5 needs `claude-agent-acp` 0.81.0 or newer. To upgrade an existing
install, run `npm install -g @agentclientprotocol/claude-agent-acp@latest`, then restart Visual Studio.
Permission requests remain interactive; choosing **Yes** approves only that request.

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
- **Slash commands:** type `/` to filter the adapter's live command catalog, plus two commands the
  sidebar always offers itself: `/login` and `/logout` (see [Getting started](#getting-started)). Use
  Up/Down to select, Tab or Enter to insert the selection without sending, and Escape to dismiss. You
  can then add arguments and send. Skill and plugin commands appear only as advertised by the backend;
  the sidebar does not provide an installed-plugin manager or a plugin reload RPC. Manage installed
  plugins through the Claude Code CLI.
- **Read and copy:** user messages appear in right-aligned bubbles; assistant responses are selectable,
  unboxed Markdown, including tables and code blocks. Both message types have a copy action that copies
  the original message text rather than rendered formatting. Markdown images display their alt text;
  the renderer does not fetch their image URLs.
- **Chat text size:** Ctrl+mouse wheel over the composer or assistant response changes the chat font
  size by one step, from 10 to 28 (default 13). This setting is local to the current chat UI session and
  does not change Visual Studio's editor zoom. Scrolling without Ctrl keeps its normal behavior.
- **History:** the clock button lists this workspace's sessions; type to filter by title or session id.
  The header shows the current session's title (first prompt, or the saved title when resumed).
- **Tasks and changed files:** the agent's task list (plan entries) and every file it edited this session
  appear as cards above the composer. Each changed file can be opened, accepted (kept) or rejected
  (restored to its pre-edit content); **Accept all** / **Reject all** apply to the whole list. The list is
  reset when you start or resume another session.
- **Implementation plan:** in **Plan** mode, when Claude asks to approve its plan an **Implementation
  Plan** document tab opens next to your code. **Proceed** approves it; **Review** sends your comments
  back so Claude revises the plan before implementing.
- **Status and usage:** while Claude works the transcript shows elapsed time, tokens consumed by the
  turn and the current activity; the ring next to the model shows how full the context window is
  (hover for numbers). The gauge button in the header opens the account's session/weekly limits;
  behind a proxy, set `HTTPS_PROXY`/`HTTP_PROXY` for Visual Studio and use Node 24 or newer, which
  is the first release whose HTTPS client honours those variables for the usage lookup.
- **Remote Control:** the **Remote Control** pill turns on driving the session from
  [claude.ai/code](https://claude.ai/code) (same bridge as the CLI's `--remote-control`); the link button
  next to it opens this session there. **Tools > Options > Claude Code > Remote Control at startup**
  turns it on for every new session. Requires the adapter installed via npm (the extension launches it
  through its bundled `claude-acp-vs.mjs`, which adds this capability on top of the stock adapter).
- **Notifications:** when Visual Studio is in the background, a Windows notification appears when
  Claude finishes, needs a permission, or has a plan to review; click it to jump back. Disable it under
  **Tools > Options > Claude Code > Notify when Visual Studio is in the background**.
- **Driving Visual Studio:** every session gets MCP tools that act on the live VS instance — open files,
  read the active document/selection, add files to projects and projects to the solution, **build /
  rebuild / clean** the solution or one project and read the **Error List** and any **Output** pane, and
  a full **debugging loop**: set breakpoints, start the startup project under the debugger, wait for a
  break, inspect the call stack, locals and arbitrary expressions, step, continue, stop. While the app
  runs Claude can list its windows, read their UI Automation tree, click buttons, toggle checkboxes,
  type into text boxes and take screenshots of them — only windows of the debugged process are reachable.
  In **Manual** mode each of these calls asks for permission first; **Accept edits** / **Auto** run
  them unprompted. See [docs/VsControlProtocol.md](docs/VsControlProtocol.md) for the full list and the
  trust boundary.

## Installing and debugging

For everyday use, open the built or release `.vsix`, select your regular Visual Studio installation in
the VSIX Installer, close Visual Studio when prompted, and restart it after installation. Building the
project alone does not install an updated extension into your regular instance.

The extension requires the **.NET 8.0 Runtime (Long Term Support)** component
(`Microsoft.NetCore.Component.Runtime.8.0`) in the target Visual Studio installation. Use
**Visual Studio Installer → Modify → Individual components** to install it; update an older Visual
Studio installation if the component is unavailable. A separately installed .NET runtime alone does
not satisfy this VSIX prerequisite. The component supplies the runtime for the framework-dependent
`ClaudeCode.VsControl.Mcp` sidecar process that every chat session spawns to drive VS automation.

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

- **CI** (`.github/workflows/ci.yml`): every PR (open/update) and push to `develop` builds the full solution
  in Release and runs all three test projects, on `windows-latest`.
- **CD** (`.github/workflows/cd.yml`): pushing a tag matching `vX.Y.Z` stamps that version into both the
  `.vsixmanifest` `Identity/@Version` (what Visual Studio's Extensions & Updates dialog displays) and the .NET
  assembly metadata (`-p:Version=X.Y.Z`), rebuilds, re-runs tests, and creates a **draft** GitHub Release with the
  built `.vsix` and its `SHA256SUMS` attached. A maintainer reviews the generated notes and the assets, then
  publishes the release by hand.
- **CodeQL** (`.github/workflows/codeql.yml`): every PR to `develop`, every push to `develop`, and a weekly
  schedule run GitHub CodeQL over C# (`build-mode: none`, no Windows build needed), the JavaScript transcript
  renderer, and the GitHub Actions workflows. Results are uploaded to the repository's **Security → Code
  scanning** tab; the job only needs `contents: read` and `security-events: write`.

## Security

The extension handles prompts, attached documents/images, editor contents (including unsaved edits),
file paths and tool results — potentially sensitive data that is passed to the ACP agent and its
configured model provider. See [SECURITY.md](SECURITY.md) for the full scope and how to report a
vulnerability.

## Contributing

Issues and pull requests are welcome. For anything beyond a small fix, please open an issue first to
discuss the change. See [AGENTS.md](AGENTS.md) for the engineering conventions this repository is held
to (TDD, SOLID, Occam's razor, and Conventional Commits).

## License

MIT — see [LICENSE.txt](LICENSE.txt).
