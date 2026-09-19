# Visual Studio control channel (named pipe, NDJSON)

Pipe name: `ClaudeCodeVs.Control.<vsPid>.<correlationId>` (created by ClaudeCode.Vsix, `PipeDirection.InOut`,
one client at a time; the server accepts connections in a loop, so a new client can reconnect after the
previous one disconnects). Framing: one JSON object per line, UTF-8. Requests are
`VsControlRequest { id, method, paramsJson }`, responses are `VsControlResponse { id, resultJson?, error? }`.
The pipe server (Vsix, main VS process) executes everything on the UI thread via
`JoinableTaskFactory.SwitchToMainThreadAsync`.

`ClaudeCode.VsControl.Mcp` is a tiny stdio MCP server: the agent's MCP tool calls arrive over stdio (MCP
JSON-RPC), get 1:1 translated to a `VsControlRequest` on the pipe, and the `VsControlResponse` becomes the
MCP tool result. It is spawned per ACP session with `--pipe <name>` and exits when stdin closes.

## Packaged runtime layout

The VSIX publishes the .NET 8 MCP executable and its runtime dependencies under `VsControlMcp/`,
alongside (not mixed with) the .NET Framework 4.8 extension assemblies at the package root. The
extension launches the MCP server from that subdirectory. Keep the published payload together when
packaging; flattening it into the root can introduce dependency collisions in the Visual Studio host.

The host's `[ProvideBindingPath]` probes the extension root. Visual Studio-provided `System.*` and
BCL runtime dependencies are intentionally excluded from the VSIX with `SuppressFromVsix`; the
standalone MCP payload retains its own published dependencies in `VsControlMcp/`.

## Trust boundary

The pipe is not just an IPC transport; it is a privilege boundary between the agent process (and,
transitively, the model/ACP agent it talks to) and the live Visual Studio host it can then command.
Four layers protect this channel:

- **ACL**: the server creates the named pipe with a `PipeSecurity` that grants `ReadWrite` only to the
  current Windows user's SID (`WindowsIdentity.User`, not the token's `Owner` SID), rejecting
  connections from other users on the same machine. The client connects with
  `PipeOptions.CurrentUserOnly` and identification-only impersonation rights.
- **Handshake token**: a per-session random 256-bit token (base64-encoded) is generated in
  `VsControlSessionRegistry.StartSession` and passed to the MCP sidecar process via the
  `CLAUDECODE_VSCONTROL_TOKEN` environment variable (`McpServerConfig.Env`), never on the command line or
  the pipe name. The client writes it as the very first line on every connection, before any MCP request;
  the server reads and compares it in constant time before processing anything else, and drops the
  connection without responding if it does not match. The token prevents a same-user process
  that only knows the pipe name from issuing commands. It is a shared bearer secret, not
  mutual process authentication; a process that obtains the token is not excluded by this check.
- **Path containment**: `openDocument`, `replaceSelection`, and `getDiagnostics` acquire a
  `WorkspacePathGuard.AcquireDocument` lease against the session workspace before calling VS APIs.
  The path must resolve inside an existing workspace to an existing file; UNC and Win32
  device-namespace paths are rejected. A missing workspace fails closed rather than authorizing the
  current directory. The Windows lease pins the resolved ancestors and file with handles that deny
  write/delete sharing, rejects reparse-point traversal during acquisition, and remains alive across
  the awaited VS operation. If the lease cannot be acquired safely, the operation is rejected.
- **Command allow-list**: `runCommand` only accepts the DTE commands listed under `runCommand` below;
  anything else is rejected before `DTE.ExecuteCommand` is ever called.

These checks are not a sandbox for the authenticated agent. In particular:

- `buildSolution` / `buildProject` build the solution currently open in Visual Studio. MSBuild
  targets, tasks, imported build files, and pre/post-build steps can execute code with the
  permissions of the Visual Studio user. Removing build/debug command names from `runCommand` does
  not remove this dedicated execution capability. Build only solutions and build dependencies you
  trust.
- `startDebugging` **runs the solution's own code** under the debugger, and `evaluateExpression`
  evaluates an arbitrary expression inside that process. Like every other tool call, these reach
  the pipe only after the chat's normal tool-permission flow (Manual mode prompts for each call;
  Accept edits/Auto modes do not), so the permission mode is the user's control over what the agent
  may launch. The debugged program is the user's own project - there is no sandbox around it.
- The UI-automation methods (`listAppWindows`, `getWindowElements`, `invokeElement`,
  `setElementValue`, `captureWindow`) are confined to the processes listed in
  `Debugger.DebuggedProcesses`: the window handle of every call is resolved against that set first,
  and a window owned by any other process (Visual Studio itself, a browser, a password manager) is
  rejected before any UI Automation call is made. Attaching to an already-running process is not
  offered, so only what Visual Studio itself launched is reachable.
- `saveAll` and the allow-listed `File.SaveAll` save dirty documents across the Visual Studio
  instance, including documents outside the session workspace.
- `getActiveDocument` and `getSelection` read the currently active editor, including unsaved
  text, without a workspace-path check. `Edit.FormatDocument` and `Edit.FormatSelection` act on
  the focused document/selection without that check. Switching focus to an out-of-workspace
  document can therefore expose or modify it through these tools.
- `listOpenDocuments`, `getBuildErrors`, and `getSolutionInfo` expose instance/solution metadata
  rather than filtering it to the session workspace. Paths and diagnostics may be sensitive.

None of this defends against a fully compromised Visual Studio process itself - the threat model is an
untrusted agent/model or another local process, not the VS host.

## Methods

- `listOpenDocuments` → `{}` → `{ documents: [{ path, isDirty, isActive }] }`
- `openDocument` → `{ path, line? }` → `{ }` — opens/activates a document, optionally moves the caret.
  `path` must resolve inside the workspace root (see Trust boundary).
- `getActiveDocument` → `{}` → `{ path, text, selectionStart, selectionEnd } | null` — reads the active
  editor buffer, including unsaved text; not restricted to the session workspace.
- `getSelection` → `{}` → `{ path, text, startLine, endLine } | null` — reads the active editor selection;
  not restricted to the session workspace.
- `replaceSelection` → `{ path, text }` → `{ }`. `path` must resolve inside the workspace root.
- `saveAll` → `{}` → `{ }` — invokes `File.SaveAll` across the VS instance, not just the workspace.
- `buildSolution` → `{ action?, configuration? }` → `{ action, succeeded, errorCount, warningCount }` — runs
  `VS.Build.BuildSolutionAsync(action)` (`build` default, `rebuild`, `clean`) and waits for completion.
  `errorCount`/`warningCount` are read back from the Error List window afterwards (subject to its own
  Build/IntelliSense scope filters), not a raw MSBuild diagnostic count. An unrecognized `configuration`
  name is silently ignored and the solution builds with whatever configuration was already active.
- `buildProject` → `{ projectName, action? }` → `{ project, action, succeeded, errorCount, warningCount }`.
- `getBuildErrors` → `{ severity? }` → `{ errors: [{ file, line, column, message, project, severity }] }` — reads
  the Error List; `severity` is `error` | `warning` | `message` and filters when given.
- `getOutput` → `{ pane?, maxChars?, clear? }` → `{ pane, text, truncated, totalChars }` or `{ pane, cleared }` —
  reads the tail (default 20 000 chars, max 200 000) of one Output window pane (`Debug` default; `Build`,
  `General`, …) or clears it. Unknown pane names fail with the list of available panes.

### Debugger

All waits poll `Debugger.CurrentMode` asynchronously (100 ms) and are capped at 120 s; the UI thread is never
blocked. Every state result is `{ mode: design | run | break, processes: [{ id, name }], reason?, currentFrame?,
timedOut? }` where `reason` is the last break reason (`breakpoint`, `step`, `exceptionThrown`, …) and
`currentFrame` is `{ function, module, language, file?, line? }`.

- `startDebugging` → `{ projectName?, configuration?, waitForBreakMs? }` → state — optionally activates a solution
  configuration and makes `projectName` the startup project, **builds first** (a failed build returns an error
  instead of letting VS pop its modal "build errors, continue?" prompt), then `Debugger.Go()` and waits for the
  process to run, then up to `waitForBreakMs` (default 3 000) for a breakpoint.
- `stopDebugging` → `{}` → state.
- `getDebuggerState` → `{}` → state.
- `setBreakpoint` → `{ path, line, condition? }` → `{ breakpoints: [...] }`. `path` must resolve inside the workspace root.
- `removeBreakpoint` → `{ path?, line? }` → `{ removed }` — all breakpoints when `path` is omitted, all on the file when
  `line` is omitted.
- `listBreakpoints` → `{}` → `{ breakpoints: [{ file, line, enabled, condition, hitCount, function }] }`.
- `continueDebugging`, `stepOver`, `stepInto`, `stepOut` → `{ waitForBreakMs? }` → state — require break mode; wait
  (default 5 000 ms) for the next break or program end, `timedOut: true` if the program is still running.
- `waitForBreak` → `{ timeoutMs? }` → state (default 10 000 ms).
- `getCallStack` → `{}` → `{ threadId, threadName, frames: [{ index, function, module, language, file?, line? }] }` (≤ 100 frames).
- `getLocals` → `{ frameIndex? }` → frame + `{ locals: [{ name, type, value, isValid }] }` (≤ 200 locals, values cut at 1 000 chars).
- `evaluateExpression` → `{ expression, timeoutMs? }` → `{ expression, name, type, value, isValid }` via
  `Debugger.GetExpression` with auto-expand rules.

### Debugged application UI (UI Automation)

- `listAppWindows` → `{}` → `{ windows: [{ hwnd, processId, title, className, bounds, isVisible, ownerHwnd }] }` —
  visible top-level windows of the debugged processes only.
- `getWindowElements` → `{ hwnd, maxDepth?, maxNodes? }` → `{ hwnd, root, truncated }` — control-view tree
  (default depth 12, 500 nodes, max 5 000); each node is `{ runtimeId, controlType, name, automationId, className,
  bounds, isEnabled, isOffscreen, actions[], value?, toggleState?, isSelected?, expandCollapseState?, children? }`.
  `actions` lists what `invokeElement`/`setElementValue` can do.
- `invokeElement` → `{ hwnd, runtimeId? | automationId? | name?, action? }` → `{ action, element }` or
  `{ action, pending: true, note }` — exactly one selector; `action` is `invoke` (default), `toggle`, `select`,
  `expand`, `collapse` or `focus`, executed through the matching UIA pattern (an unsupported pattern fails with the
  list of supported ones). WPF providers block `Invoke`/`SetValue` until the app's handler returns, so when the click
  lands on a breakpoint the call reports `pending` after 5 s instead of hanging; the action has still happened.
- `setElementValue` → `{ hwnd, runtimeId? | automationId? | name?, value }` → `{ element }` or `{ pending, note }`
  via `ValuePattern`.

All UI Automation work runs on a background thread; the VS UI thread is never blocked by a stalled target app.
- `captureWindow` → `{ hwnd }` → `{ hwnd, width, height, scale, _image: { mimeType, data } }` — `PrintWindow`
  screenshot (screen copy fallback), downscaled so the longer side is ≤ 1 920 px. The `_image` member is lifted out
  by the MCP server into an `image` content block (see below).
- `getDiagnostics` → `{ path }` → `{ diagnostics: [{ line, column, message, severity, source }] }` —
  language-service squiggles for one file. `path` must resolve inside the workspace root.
- `runCommand` → `{ commandName, args? }` → `{ }` — invokes one DTE command from a fixed allow-list:
  `Edit.FormatDocument`, `Edit.FormatSelection`, `Debug.StopDebugging`, `File.SaveAll`, `View.ErrorList`.
  Commands that would execute code from the open solution (`Debug.Start`,
  `Debug.StartWithoutDebugging`, `Build.BuildSolution`, `Build.RebuildSolution`) are deliberately not
  allow-listed here; the dedicated `buildSolution` tool still executes the solution's MSBuild logic.
  Formatting commands operate on the focused editor; `File.SaveAll` is instance-wide.
- `getSolutionInfo` → `{}` → `{ solutionPath, projects: [{ name, path }] }`
- `addFileToProject` → `{ projectName, path }` → `{ project, path }` — includes an existing on-disk file in the
  named project (`Project.AddExistingFilesAsync`); needed for non-SDK-style projects. `path` must resolve
  inside the workspace root and already exist.
- `addProjectToSolution` → `{ path }` → `{ name, path }` — adds an existing project file to the open solution
  (`DTE.Solution.AddFromFile`). `path` must resolve inside the workspace root.
- `openSolution` → `{ path }` → `{ solutionPath, projects }` — closes the current solution (saving first) and opens
  the given `.sln`/`.slnx` (`DTE.Solution.Open`). `path` must resolve inside the workspace root; this is how a
  solution the agent just created on disk becomes the one the build/debugger tools act on.

Errors (file not found, ambiguous command, path outside the workspace, command not allow-listed, build
already running, etc.) are returned via `VsControlResponse.error` and surfaced to the agent as an MCP tool
error, never thrown across the pipe as an exception.

Tool result text returned to the model is capped at 256 KiB and wrapped in
`<<<UNTRUSTED_TOOL_OUTPUT>>> ... <<<END_UNTRUSTED_TOOL_OUTPUT>>>` delimiters (`McpServer.CreateToolResult`):
it can originate from workspace files, the active editor, or other Visual Studio instance state and
must be treated as data, not as instructions.

A successful result object may carry one binary attachment under `_image: { mimeType, data }` (base64). The
MCP server removes it from the text and appends an MCP `image` content block after the text block, so the
model sees the picture itself and the 256 KiB text cap never truncates it.
