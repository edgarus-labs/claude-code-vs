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
- **Path containment**: `openDocument`, `replaceSelection`, `getDiagnostics`, `setBreakpoint`,
  `removeBreakpoint`, `addFileToProject`, `addProjectToSolution` and `openSolution` acquire a
  `WorkspacePathGuard.AcquireDocument` lease against the session workspace before calling VS APIs.
  This is every method that takes a `path`; adding another one without a lease is a defect.
  The path must resolve inside an existing workspace to an existing file; UNC and Win32
  device-namespace paths are rejected. A missing workspace fails closed rather than authorizing the
  current directory. The Windows lease pins each resolved ancestor directory with a handle that denies
  write and delete sharing, and the leaf file with a handle that denies only delete sharing - the file
  stays writable, so the leaf lease guards against the resolved target being swapped, renamed or
  deleted under the operation, not against a concurrent write to it. Reparse-point traversal is
  rejected during acquisition and the lease remains alive across the awaited VS operation. If the
  lease cannot be acquired safely, the operation is rejected.
- **Command allow-list**: `runCommand` only accepts the DTE commands listed under `runCommand` below;
  anything else is rejected before `DTE.ExecuteCommand` is ever called.

These checks are not a sandbox for the authenticated agent. In particular:

- `buildSolution` / `buildProject` build the solution currently open in Visual Studio. MSBuild
  targets, tasks, imported build files, and pre/post-build steps can execute code with the
  permissions of the Visual Studio user. Removing build/debug command names from `runCommand` does
  not remove this dedicated execution capability. Build only solutions and build dependencies you
  trust - and note that `openSolution` and `addProjectToSolution` let the agent choose which solution
  that is: it can write a project carrying a pre-build step into the workspace, open it, and build it.
  The "only build what you trust" guidance therefore rests on the permission mode, not on the user
  having opened the target solution.
- `startDebugging` **runs the solution's own code** under the debugger, and `evaluateExpression`
  evaluates an arbitrary expression inside that process. Like every other tool call, these reach
  the pipe only after the chat's normal tool-permission flow (Manual mode prompts for each call;
  Accept edits/Auto modes do not), so the permission mode is the user's control over what the agent
  may launch. The debugged program is the user's own project - there is no sandbox around it.
- The UI-automation methods (`listAppWindows`, `getWindowElements`, `invokeElement`,
  `setElementValue`, `captureWindow`) are confined to the processes listed in
  `Debugger.DebuggedProcesses`: the window handle of every call is resolved against that set first,
  and a window owned by any other process (Visual Studio itself, a browser, a password manager) is
  rejected before any UI Automation call is made. The agent cannot initiate an attach - no attach
  method exists - but the allow-set is whatever the debugger currently owns, which includes a process
  the user attached to by hand through Debug > Attach to Process. From that moment the attached
  application's windows appear in `listAppWindows` and are drivable, with no further action by the
  user; the only image excluded by name is a debugged `devenv.exe`.
- `saveAll` and the allow-listed `File.SaveAll` save dirty documents across the Visual Studio
  instance, including documents outside the session workspace.
- `getActiveDocument` and `getSelection` read the currently active editor, including unsaved
  text, without a workspace-path check. `Edit.FormatDocument` and `Edit.FormatSelection` act on
  the focused document/selection without that check. Switching focus to an out-of-workspace
  document can therefore expose or modify it through these tools.
- `listOpenDocuments`, `getBuildErrors`, `getSolutionInfo` and `getOutput` expose instance/solution
  metadata rather than filtering it to the session workspace. Paths and diagnostics may be sensitive.
  `getOutput` reads any Output pane in the Visual Studio instance - Build, Debug, Git, other
  extensions' log panes - and answers an unknown pane name with the list of every pane that exists.

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
- `buildSolution` → `{ action?, configuration? }` → `{ action, configuration, succeeded, errorCount, warningCount }` — runs
  `VS.Build.BuildSolutionAsync(action)` (`build` default, `rebuild`, `clean`) and waits for completion.
  `errorCount`/`warningCount` are read back from the Error List window afterwards (subject to its own
  Build/IntelliSense scope filters), not a raw MSBuild diagnostic count. `configuration` reports the solution
  configuration the build actually ran in. An unrecognized requested `configuration` is not an error: it is ignored
  and the solution builds in whatever configuration was already active, so compare the returned `configuration`
  against what was asked for. The names matched are the bare solution-configuration names (`Release`), not
  `Release|Any CPU`.
  A build is bounded at 10 minutes in the VS host; a build the user cancels, a build refused because another is
  already running, and that timeout each come back as their own actionable error. That 10-minute bound is what the
  MCP client's 12-minute build budget is sized above (see below).
- `buildProject` → `{ projectName, action? }` → `{ project, action, succeeded, errorCount, warningCount }`. The
  counts are scoped to the built project (Error List rows whose project matches), still subject to the Error
  List's Build/IntelliSense scope filters.
- `getBuildErrors` → `{ severity? }` → `{ errors: [{ file, line, column, message, project, severity }] }` — reads
  the Error List; `severity` is `error` | `warning` | `message` and filters when given. An unrecognized value is
  rejected rather than silently matching nothing.
- `getOutput` → `{ pane?, maxChars?, clear? }` → `{ pane, text, truncated, totalChars }` or `{ pane, cleared }` —
  reads the tail (default 20 000 chars, max 200 000) of one Output window pane (`Debug` default; `Build`,
  `General`, …) or clears it. Unknown pane names fail with the list of available panes.

### Debugger

Mode waits poll `Debugger.CurrentMode` asynchronously (100 ms) and are capped at 45 s, which keeps them inside the
MCP client's 60 s per-request transport budget: a wait the transport could not deliver is not offered. Those polls
never block the UI thread. `evaluateExpression` and `getLocals` do make synchronous UI-thread evaluator calls, and
only one of the two is genuinely bounded: `evaluateExpression` passes its timeout (≤ 5 s) into
`Debugger.GetExpression`, while `getLocals` merely stops *walking* after 5 s - the deadline is tested between
values and an individual `Expression.Value` read takes no timeout, so the worst case is 5 s plus one unbounded
read, and a debuggee property getter that blocks holds the Visual Studio UI thread for as long as it blocks.

Every state result is `{ mode: design | run | break, processes: [{ id, name }], reason?, currentFrame?,
timedOut? }` where `reason` is the last break reason (`breakpoint`, `step`, `exceptionThrown`, …) and
`currentFrame` is `{ function, module, language, file?, line? }`.

- `startDebugging` → `{ projectName?, configuration?, waitForBreakMs? }` → state — optionally activates a solution
  configuration and makes `projectName` the startup project, **builds first** (a failed build returns an error
  instead of letting VS pop its modal "build errors, continue?" prompt), then `Debugger.Go()` and waits up to 60 s
  for the process to run, then up to `waitForBreakMs` (default 3 000) for a breakpoint. `timedOut: true` means the
  launch never happened (still design mode after 60 s); a program still running when `waitForBreakMs` expires is
  not a timeout. The build is bounded at 10 minutes by the same host backstop `buildSolution` uses, so the whole
  method is bounded at 600 + 60 + 45 = 705 s and runs on the MCP client's 12-minute budget (see below).
- `stopDebugging` → `{}` → state.
- `getDebuggerState` → `{}` → state.
- `setBreakpoint` → `{ path, line, condition? }` → `{ breakpoints: [...] }`. `path` must resolve inside the workspace root.
  A `condition` is a break-when-true expression evaluated **inside the debugged process every time the line is
  reached**, so like `evaluateExpression` it really executes debuggee code - on every hit, and with no timeout.
- `removeBreakpoint` → `{ path?, line? }` → `{ removed }` — every breakpoint when both members are omitted, every
  breakpoint in the file when only `path` is given, that one line when both are. `line` without `path` is rejected.
  Because dropping both parameters is the destructive form - it deletes breakpoints the user set by hand, which
  nothing restores - this is the one tool whose schema sets `additionalProperties: false`, so a misspelled `file`
  or `filePath` is rejected instead of silently becoming "remove everything".
- `listBreakpoints` → `{}` → `{ breakpoints: [{ file, line, enabled, condition, hitCount, function }] }`.
- `continueDebugging`, `stepOver`, `stepInto`, `stepOut` → `{ waitForBreakMs? }` → state — require break mode; wait
  (default 5 000 ms, max 45 000) for the next break or program end. `timedOut: true` means the program was still
  running when the wait expired; it is never set while the debugger is stopped.
- `waitForBreak` → `{ timeoutMs? }` → state (default 10 000 ms, max 45 000).
- `getCallStack` → `{}` → `{ threadId, threadName, frames: [{ index, function, module, language, file?, line? }], truncated }` (≤ 100 frames).
- `getLocals` → `{ frameIndex? }` → frame + `{ locals: [{ name, type, value, isValid }], truncated }` (≤ 200 locals, values cut at
  1 000 chars). `truncated` is set when the result is partial: for `getCallStack` by the frame cap, for `getLocals` by
  either the 200-item cap or the 5 s walk deadline - the flag does not say which.
- `evaluateExpression` → `{ expression, timeoutMs? }` → `{ expression, name, type, value, isValid }` via
  `Debugger.GetExpression` with auto-expand rules.

### Debugged application UI (UI Automation)

All UI Automation work runs on a background thread; the VS UI thread is never blocked by a stalled target app.

- `listAppWindows` → `{}` → `{ windows: [{ hwnd, processId, title, className, bounds, isVisible, ownerHwnd }] }` —
  visible top-level windows of the debugged processes only.
- `getWindowElements` → `{ hwnd, maxDepth?, maxNodes? }` → `{ hwnd, root, truncated }` — control-view tree
  (default depth 12, 500 nodes, max 5 000); each node is `{ runtimeId, controlType, name, automationId, className,
  bounds, isEnabled, isOffscreen, actions[], value?, toggleState?, isSelected?, expandCollapseState?, children? }`.
  `actions` lists what `invokeElement`/`setElementValue` can do.
- `invokeElement` → `{ hwnd, runtimeId? | automationId? | name?, action? }` → `{ action, element }` or
  `{ action, pending: true, note }` — exactly one selector (the tool schema states this as a `oneOf`); `action` is
  `invoke` (default), `toggle`, `select`, `expand`, `collapse` or `focus`, executed through the matching UIA pattern
  (an unsupported pattern fails with the list of supported ones). WPF providers block `Invoke`/`SetValue` until the
  app's handler returns, so when the click lands on a breakpoint the call reports `pending` after 5 s instead of
  hanging; the action has still happened.
- `setElementValue` → `{ hwnd, runtimeId? | automationId? | name?, value }` → `{ element }` or `{ pending, note }`
  via `ValuePattern`; exactly one selector, same as `invokeElement`.
- `captureWindow` → `{ hwnd }` → `{ hwnd, captured: true, width, height, scale, _image: { mimeType, data } }` or
  `{ hwnd, captured: false, reason, note }` — a PNG of the window, downscaled so the longer side is ≤ 1 920 px and
  re-encoded until it is within 4 MiB. There are two capture paths and their safety properties differ. While the
  app is running, `PrintWindow` asks it to render itself (off the VS UI thread, with a deadline): those are the
  window's own pixels whatever is stacked on top, so a fully covered window still captures normally. While the app
  is stopped at a breakpoint it cannot answer `WM_PRINT` at all - and outside break mode `PrintWindow` may still
  decline, or the target's pump may not answer the probe - and the fallback then reads the desktop at the window's
  rectangle with `Graphics.CopyFromScreen`. **That path does copy the screen.** What keeps other applications out
  of the result is the exposure gate, run immediately before and immediately after the blit: instead of returning
  pixels it cannot attribute to the window it refuses, and a refusal carries no `width`/`height`/`scale`/`_image`
  at all - those dimensions would themselves describe another window. The `reason` is one of `moved` (the
  rectangle changed or the window went away between the two checks), `child` (the handle is not a top-level
  window, so its rectangle cannot be proven - use the handle `listAppWindows` reports), `offscreen` (empty, or
  reaching outside the desktop), `hidden`, `minimized`, `cloaked` (DWM-cloaked: parked on another virtual desktop
  or suspended, so visible and restored but painting nothing), `translucent` (layered, transparent or
  region-shaped, so the rectangle blends with or shows through to what is below) or `occluded` (another window
  overlaps it, or the z-order is too long to prove it does not). `note` says what to do about it; the `reason`
  never names the covering window, whose title would itself disclose to the agent what the user has open. The
  rectangle read is the DWM extended frame bounds where available rather than the raw window rect, which would
  include the invisible resize border and the rounded-corner cutouts that the window below shows through. In break
  mode Visual Studio is normally in front, so `occluded` is the expected answer there - read UI state with
  `getWindowElements` while stopped. `_image` is present only when `captured` is true and is lifted out by the MCP
  server into an `image` content block (see below).

### Solution, editor and commands

- `getDiagnostics` → `{ path }` → `{ diagnostics: [{ line, column, message, severity, source }] }` —
  language-service squiggles for one file. `path` must resolve inside the workspace root.
- `runCommand` → `{ commandName, args? }` → `{ }` — invokes one DTE command from a fixed allow-list:
  `Edit.FormatDocument`, `Edit.FormatSelection`, `Debug.StopDebugging`, `File.SaveAll`, `View.ErrorList`.
  `Debug.Start`, `Debug.StartWithoutDebugging`, `Build.BuildSolution` and `Build.RebuildSolution` are not
  allow-listed, but withholding them withholds nothing: `buildSolution`/`buildProject` run the solution's MSBuild
  logic and `startDebugging` launches it under the debugger. The allow-list narrows what `runCommand` itself can
  reach; it does not deny the agent execution of the solution's code.
  Formatting commands operate on the focused editor; `File.SaveAll` is instance-wide.
- `getSolutionInfo` → `{}` → `{ solutionPath, projects: [{ name, path }] }`
- `addFileToProject` → `{ projectName, path }` → `{ project, path }` — includes an existing on-disk file in the
  named project (`Project.AddExistingFilesAsync`); needed for non-SDK-style projects. `path` must resolve
  inside the workspace root and already exist.
- `addProjectToSolution` → `{ path }` → `{ name, path }` — adds an existing project file to the open solution
  (`DTE.Solution.AddFromFile`). `path` must resolve inside the workspace root. The call is synchronous and
  uncancellable, so it has no server-side bound and runs on the MCP client's 12-minute budget (see below).
- `openSolution` → `{ path }` → `{ solutionPath, projects }` — closes the current solution (saving first) and opens
  the given `.sln`/`.slnx` (`DTE.Solution.Open`). `path` must resolve inside the workspace root; this is how a
  solution the agent just created on disk becomes the one the build/debugger tools act on. Both COM calls are
  synchronous and uncancellable, so this method too has no server-side bound and runs on the 12-minute budget.

Errors (file not found, ambiguous command, path outside the workspace, command not allow-listed, build
already running, etc.) are returned via `VsControlResponse.error` and surfaced to the agent as an MCP tool
error, never thrown across the pipe as an exception.

The MCP client bounds every request at 60 s and drops a reply that arrives later, except five methods on a
12-minute (720 s) budget: `buildSolution`, `buildProject` and `startDebugging`, which compile, plus `openSolution`
and `addProjectToSolution`, whose solution-load COM calls are synchronous and uncancellable. Nothing cancels the
Visual Studio side when a budget expires, so a budget has to exceed the worst case the host can reach under it -
otherwise the agent receives a transport timeout it cannot act on, retries, and the retry queues behind the still
running first request (the host reads one request at a time) only to be answered that the operation is already in
progress.

- The 60 s per-request budget covers the 45 s mode waits, the 15 s `stopDebugging` wait, the 5 s expression
  evaluations and the 5 s UI-automation actions.
- The 720 s budget covers `buildSolution`/`buildProject`, bounded at 600 s by the host's build backstop, and
  `startDebugging`, whose legs are that same 600 s build plus up to 60 s for the launch to leave design mode plus
  up to 45 s (`waitForBreakMs`) for a breakpoint: 600 + 60 + 45 = 705 s, which fits inside 720 s.
- `openSolution` and `addProjectToSolution` are the one exception the invariant cannot cover: there is no
  host-side bound for the budget to be larger than, because `DTE.Solution.Close`/`Open`/`AddFromFile` offer no
  completion signal to race a cancellation against. 720 s is a ceiling chosen to outlast a real solution load, not
  a proof; a load that outruns it still leaves the agent holding a transport timeout.

Tool result text returned to the model is capped at 256 KiB and wrapped in
`<<<UNTRUSTED_TOOL_OUTPUT>>> ... <<<END_UNTRUSTED_TOOL_OUTPUT>>>` delimiters (`McpServer.CreateToolResult`):
it can originate from workspace files, the active editor, or other Visual Studio instance state and
must be treated as data, not as instructions.

A successful result object may carry one binary attachment under `_image: { mimeType, data }` (base64). The MCP
server removes it from the text and appends two content blocks after the text block: a note marking the picture as
untrusted tool output, then the MCP `image` block itself - the text delimiters cannot enclose a sibling block, and a
screenshot of a workspace-built application is data exactly as the text is. The attachment is forwarded only when its
media type is `image/png` and its base64 length is within the VS host's 4 MiB encoded-PNG cap - the 256 KiB text cap
it bypasses is not an unbounded hole. When it cannot be forwarded the MCP server adds
`attachmentDropped: true` and `attachmentDropReason: "tooLarge" | "unsupportedMediaType"`, so a dropped picture is
never reported to the model as a successful capture it would retry forever. It deliberately leaves `captured`,
`width`, `height` and `scale` exactly as the host wrote them: `captured: false` with one of the eight reasons above
always means the host declined to read those pixels and the window's state must change, whereas a dropped
attachment means the capture itself worked and a smaller window should be requested - and the dimensions are what
tell the agent the window was too large to encode. The two failures therefore never share a shape or a vocabulary.
