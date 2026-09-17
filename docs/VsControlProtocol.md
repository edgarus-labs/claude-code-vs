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
Three independent layers protect it:

- **ACL**: the server creates the named pipe with a `PipeSecurity` that grants `ReadWrite` only to the
  current Windows user's SID (the user running this VS instance), rejecting connections from other
  users on the same machine. The client connects with `PipeOptions.CurrentUserOnly`, which enforces the
  same restriction from its side.
- **Handshake token**: a per-session random 256-bit token (base64-encoded) is generated in
  `VsControlSessionRegistry.StartSession` and passed to the MCP sidecar process via the
  `CLAUDECODE_VSCONTROL_TOKEN` environment variable (`McpServerConfig.Env`), never on the command line or
  the pipe name. The client writes it as the very first line on every connection, before any MCP request;
  the server reads and compares it in constant time before processing anything else, and drops the
  connection without responding if it does not match. This defends against another process running as
  the *same* Windows user (which the ACL alone would not stop) guessing the pipe name.
- **Path containment**: `openDocument`, `replaceSelection`, and `getDiagnostics` resolve their `path`
  argument through `WorkspacePathGuard.TryResolveWithinWorkspace` against the workspace root the ACP
  session was opened with, and reject anything that resolves outside it (including UNC and Win32
  device-namespace paths) before touching any VS API.
- **Command allow-list**: `runCommand` only accepts the DTE commands listed under `runCommand` below;
  anything else is rejected before `DTE.ExecuteCommand` is ever called.

None of this defends against a fully compromised Visual Studio process itself - the threat model is an
untrusted agent/model or another local process, not the VS host.

## Methods

- `listOpenDocuments` → `{}` → `{ documents: [{ path, isDirty, isActive }] }`
- `openDocument` → `{ path, line? }` → `{ }` — opens/activates a document, optionally moves the caret.
  `path` must resolve inside the workspace root (see Trust boundary).
- `getActiveDocument` → `{}` → `{ path, text, selectionStart, selectionEnd } | null`
- `getSelection` → `{}` → `{ path, text, startLine, endLine } | null`
- `replaceSelection` → `{ path, text }` → `{ }`. `path` must resolve inside the workspace root.
- `saveAll` → `{}` → `{ }`
- `buildSolution` → `{ configuration? }` → `{ succeeded, errorCount, warningCount }` — runs
  `VS.Build.BuildSolutionAsync()` and waits for completion. `errorCount`/`warningCount` are read back from
  the Error List window afterwards (subject to its own Build/IntelliSense scope filters), not a raw
  MSBuild diagnostic count. An unrecognized `configuration` name is silently ignored and the solution
  builds with whatever configuration was already active.
- `getBuildErrors` → `{}` → `{ errors: [{ file, line, column, message, severity }] }` — reads the Error List.
- `getDiagnostics` → `{ path }` → `{ diagnostics: [{ line, column, message, severity, source }] }` —
  language-service squiggles for one file. `path` must resolve inside the workspace root.
- `runCommand` → `{ commandName, args? }` → `{ }` — invokes one DTE command from a fixed allow-list:
  `Edit.FormatDocument`, `Edit.FormatSelection`, `Debug.StopDebugging`, `File.SaveAll`, `View.ErrorList`.
  Commands that would execute code from the open solution (`Debug.Start`,
  `Debug.StartWithoutDebugging`, `Build.BuildSolution`, `Build.RebuildSolution`) are deliberately not
  allow-listed here; use the dedicated `buildSolution` tool to build.
- `getSolutionInfo` → `{}` → `{ solutionPath, projects: [{ name, path }] }`

Errors (file not found, ambiguous command, path outside the workspace, command not allow-listed, build
already running, etc.) are returned via `VsControlResponse.error` and surfaced to the agent as an MCP tool
error, never thrown across the pipe as an exception.

Tool result text returned to the model is capped at 256 KiB and wrapped in
`<<<UNTRUSTED_TOOL_OUTPUT>>> ... <<<END_UNTRUSTED_TOOL_OUTPUT>>>` delimiters (`McpServer.CreateToolResult`):
it originates from the open workspace/solution and must be treated as data, not as instructions.
