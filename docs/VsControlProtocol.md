# Visual Studio control channel (named pipe, NDJSON)

Pipe name: `ClaudeCodeVs.Control.<vsPid>.<sessionId>` (created by ClaudeCode.Vsix, `PipeDirection.InOut`, one client).
Framing: one JSON object per line, UTF-8. Requests are `VsControlRequest { id, method, paramsJson }`,
responses are `VsControlResponse { id, resultJson?, error? }`. The pipe server (Vsix, main VS process)
executes everything on the UI thread via `JoinableTaskFactory.SwitchToMainThreadAsync`.

`ClaudeCode.VsControl.Mcp` is a tiny stdio MCP server: the agent's MCP tool calls arrive over stdio (MCP
JSON-RPC), get 1:1 translated to a `VsControlRequest` on the pipe, and the `VsControlResponse` becomes the
MCP tool result. It is spawned per ACP session with `--pipe <name>` and exits when stdin closes.

## Methods

- `listOpenDocuments` → `{}` → `{ documents: [{ path, isDirty, isActive }] }`
- `openDocument` → `{ path, line? }` → `{ }` — opens/activates a document, optionally moves the caret.
- `getActiveDocument` → `{}` → `{ path, text, selectionStart, selectionEnd } | null`
- `getSelection` → `{}` → `{ path, text, startLine, endLine } | null`
- `replaceSelection` → `{ path, text }` → `{ }`
- `saveAll` → `{}` → `{ }`
- `buildSolution` → `{ configuration? }` → `{ succeeded, errorCount, warningCount }` — runs `Build.SolutionBuild`, waits for completion.
- `getBuildErrors` → `{}` → `{ errors: [{ file, line, column, message, severity }] }` — reads the Error List.
- `getDiagnostics` → `{ path }` → `{ diagnostics: [{ line, column, message, severity, source }] }` — language-service squiggles for one file.
- `runCommand` → `{ commandName, args? }` → `{ }` — invokes an arbitrary DTE command (e.g. `Edit.FormatDocument`, `Debug.Start`), allow-listed by the Vsix side.
- `getSolutionInfo` → `{}` → `{ solutionPath, projects: [{ name, path }] }`

Errors (file not found, ambiguous command, build already running, etc.) are returned via `VsControlResponse.error`
and surfaced to the agent as an MCP tool error, never thrown across the pipe as an exception.
