# Claude Code for Visual Studio

Native port of the [Claude Code](https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code) VS Code
extension to Visual Studio 2022/2026, as a real sidebar tool window (WPF, docked like Solution Explorer) — not a
terminal wrapper. The agent runs out-of-process as `claude-code-acp` (or `claude --acp`), speaking the
[Agent Client Protocol](https://agentclientprotocol.com) (JSON-RPC 2.0 over stdio); the extension is a full ACP
*client*: it streams responses, renders diffs and tool calls, brokers file reads/writes, and answers permission
prompts natively in VS UI, with no CLI/terminal ever shown to the user.

Authentication is OAuth-only, via the Claude Code CLI's own browser login (`claude setup-token`) — no API key is
ever entered into or stored by the extension itself; the resulting token is DPAPI-encrypted at
`%LOCALAPPDATA%\ClaudeCodeVs\auth.bin` and passed to the agent process as `CLAUDE_CODE_OAUTH_TOKEN` (never as a CLI
argument).

Claude can also *drive Visual Studio itself*: every chat session injects a client-side MCP server
(`ClaudeCode.VsControl.Mcp`) exposing VS automation — open/activate documents, read/replace the selection, run a
build, read the Error List and editor squiggles, run DTE commands, inspect solution structure — as ordinary MCP
tools (see `docs/VsControlProtocol.md` for the exact wire contract).

## Solution layout

| Project | TFM | Role |
|---|---|---|
| `src/ClaudeCode.Contracts` | netstandard2.0 | Cross-project interfaces/DTOs (ACP connection, auth, MCP config) — the only thing every other project depends on. |
| `src/ClaudeCode.Acp` | netstandard2.0 | ACP client: JSON-RPC/stdio transport, process spawning, executable resolution. |
| `src/ClaudeCode.Core.ViewModels` | netstandard2.0 | XAML-free chat MVVM (view models, demo/fake connection). |
| `src/ClaudeCode.Core` | net472 + WPF | The sidebar UI (`ChatPanelView` and friends). |
| `src/ClaudeCode.VsControl.Mcp` | net8.0 (exe) | Standalone MCP-over-stdio server; forwards tool calls to VS over a named pipe. |
| `src/ClaudeCode.Vsix` | net48 | The actual VSIX package: `AsyncPackage`, tool window, commands, options page, OAuth sign-in, the named-pipe server VsControl.Mcp talks to. |

## Building

Full C# compilation and all unit tests run cross-platform (`dotnet build ClaudeCodeVS.slnx`,
`dotnet test <project>`). **Producing and running the actual `.vsix`, and packaging steps
(`CreateVsixContainer`/`GeneratePkgDefFile`/`VSCTCompile`) driven by `Microsoft.VSSDK.BuildTools`, require
Windows with Visual Studio 2022/2026 and the "Visual Studio extension development" workload installed** — those
MSBuild tasks are desktop-CLR/VS-registry-dependent and no-op silently on other platforms. On Windows:

```powershell
dotnet restore ClaudeCodeVS.slnx
msbuild ClaudeCodeVS.slnx -t:Build -p:Configuration=Release
# .vsix lands in src/ClaudeCode.Vsix/bin/Release/net48/
```

Press F5 on `ClaudeCode.Vsix` in Visual Studio to launch the experimental instance for interactive debugging.

## CI/CD

- **CI** (`.github/workflows/ci.yml`): every PR (open/update) and push to `develop`/`main` builds the full solution
  in Release and runs all three test projects, on `windows-latest`.
- **CD** (`.github/workflows/cd.yml`): pushing a tag matching `vX.Y.Z` stamps that version into both the
  `.vsixmanifest` `Identity/@Version` (what Visual Studio's Extensions & Updates dialog displays) and the .NET
  assembly metadata (`-p:Version=X.Y.Z`), rebuilds, re-runs tests, and publishes a GitHub Release with the built
  `.vsix` attached.
