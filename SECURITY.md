# Security Policy

Claude Code for Visual Studio is a development-stage extension. If you discover a security
vulnerability, please report it privately rather than opening a public issue with exploit
details.

## Reporting a vulnerability

- Preferred: use GitHub's private vulnerability reporting for this repository
  ([github.com/edgarus-labs/claude-code-vs/security/advisories/new](https://github.com/edgarus-labs/claude-code-vs/security/advisories/new)),
  which is visible only to maintainers, not a public issue, or
- Contact the maintainers listed in the repository (see the `Publisher`/organization on
  [github.com/edgarus-labs](https://github.com/edgarus-labs)).

Please include the affected version/commit, a description of the issue, and reproduction
steps or a proof of concept. We will acknowledge reports and work with you on a fix and
disclosure timeline before any public details are published.

## Scope

This repository has no hosted service and no PII surface. In-scope concerns include (but are
not limited to): the Visual Studio extension (`ClaudeCode.Vsix`), the ACP transport
(`ClaudeCode.Acp`), the VS-control MCP bridge (`ClaudeCode.VsControl.Mcp`), and the CI/CD
workflows that build and publish releases.
