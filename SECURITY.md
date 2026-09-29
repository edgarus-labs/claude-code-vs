# Security Policy

Claude Code for Visual Studio is a development-stage extension. If you discover a security
vulnerability, please report it privately rather than opening a public issue with exploit
details.

## Reporting a vulnerability

This repository is public. Report vulnerabilities privately through GitHub's
[private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository):
**Security → Report a vulnerability** on this repository. Never post exploit details,
credentials, or personal data in a public issue.

Include the affected version/commit, a description of the issue, and reproduction steps or a
proof of concept, with unrelated sensitive data removed.

## Scope

This repository does not operate a hosted service, but the extension handles potentially
sensitive data: prompts, attached documents and images, editor contents (including unsaved
edits), file paths, diagnostics, and tool results. These may contain personal information,
credentials, or confidential source code. Context and tool results are passed to the ACP
agent and may be sent to its configured model provider; do not treat local IPC as a
guarantee that data stays on the machine.

The sidebar's `/login` and `/logout` commands launch the same resolved, trusted adapter
executable the sign-in status probe already uses (never a path or command derived from
untrusted input) to run its bundled CLI auth commands. The extension process itself never
reads, logs, or stores the resulting credentials; only the process exit code and the existing
status probe's JSON cross back into the extension. When `auth logout` fails, the last 4 KB of its
stderr is written to the Visual Studio ActivityLog for diagnosis (never shown in the UI).

With **Auto effort** selected, each message is also judged before it is sent: the extension runs
the same resolved adapter executable's bundled CLI in print mode (`--cli -p --model haiku`, so
Claude Haiku by default) with no tools, no settings files, no MCP servers and no saved session,
from the system temporary directory rather than your workspace (the CLI may still read user-level
instruction files such as a CLAUDE.md in your home directory or above the temporary directory). The CLI inherits Visual Studio's environment, so
the message goes to whichever model provider your Claude Code is configured for. The message
(shortened to at most 2,000 characters) goes to the CLI on standard input as untrusted data to
judge, never on the command line; only the current message is sent, without attachments or
conversation history. The shortening removes noise such as ANSI escapes, XML envelopes and closed
fenced code (unless that would leave almost nothing); it is not a redaction step, so anything else you type is sent as written. The judge's only
effect is choosing the `low`/`medium`/`high` effort for that message.

In-scope concerns include (but are not limited to): the Visual Studio extension
(`ClaudeCode.Vsix`), the ACP transport (`ClaudeCode.Acp`), the VS-control MCP bridge
(`ClaudeCode.VsControl.Mcp`), and the CI/CD workflows that build and publish releases.
