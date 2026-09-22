# Security Policy

Claude Code for Visual Studio is a development-stage extension. If you discover a security
vulnerability, please report it privately rather than opening a public issue with exploit
details.

## Reporting a vulnerability

This repository is private. GitHub's
[private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)
is available for public repositories, so it is not a reporting route for this repository.
No dedicated private reporting address is currently published in this repository or the
[Edgarus Labs organization profile](https://github.com/edgarus-labs). There is therefore no
documented private reporting route at present; a maintainer must provide one before this
gap can be resolved. Do not post exploit details, credentials, or personal data in an
issue, and do not assume an issue is visible only to security maintainers.

Once a private channel is established, include the affected version/commit, a description
of the issue, and reproduction steps or a proof of concept, with unrelated sensitive data
removed.

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
status probe's JSON cross back into the extension.

In-scope concerns include (but are not limited to): the Visual Studio extension
(`ClaudeCode.Vsix`), the ACP transport (`ClaudeCode.Acp`), the VS-control MCP bridge
(`ClaudeCode.VsControl.Mcp`), and the CI/CD workflows that build and publish releases.
