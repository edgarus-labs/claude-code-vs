# Agent instructions — Claude Code for Visual Studio

Instructions for any AI agent (or human) making changes in this repository. These are binding
engineering rules, not suggestions. If a change cannot satisfy all of them, stop and say so
explicitly rather than silently cutting a corner.

## TDD — Test-Driven Development is mandatory

Every behavioral change follows red → green → refactor:

1. **Red**: write a test that fails for the right reason before writing the fix. Run it and
   confirm the failure message matches the defect, not a compile error or an unrelated crash.
2. **Green**: write the smallest change that makes the test pass. Do not gold-plate.
3. **Refactor**: clean up only what the change touched, with tests still green after each step.

Exceptions are narrow and must be stated explicitly in the PR description, not silently skipped:

- **Genuinely untestable code** (COM event sinks, live VS SDK/WPF visual tree, CI YAML) — extract
  every bit of pure logic you can into a plain, dependency-free method or function and test *that*.
  What remains as untestable glue must be named precisely (e.g. "requires a live `Dispatcher`
  pumped by `Application.Run`, unavailable in this repo's headless CI") — never "hard to test".
- **Config-only changes** (`.editorconfig`, `.csproj` properties, `.vsixmanifest`, GitHub Actions
  YAML) have no unit-test equivalent. Verify them by re-reading the file after the edit and, where
  possible, by running the actual command/script locally (e.g. exercise a regex against the exact
  malicious input it defends against) and recording the result.
- A test that pins implementation details (field copies, mock echoes, forwarded values, source
  text) is not a test — delete it. A test must fail on a plausible bug in observable behavior, not
  on an incidental refactor.

Regression bugs get a test that fails before the fix and passes after — keep it as the permanent
regression guard.

## SOLID and general design

- **Single Responsibility**: a class changes for one reason. If a fix requires touching unrelated
  responsibilities in the same file, that is a signal the class is already too broad — flag it,
  don't compound it.
- **Open/Closed**: prefer extension points (interfaces, virtual members) already in the codebase
  over widening `switch`/`if` chains across unrelated concerns.
- **Liskov Substitution**: implementations of an interface (e.g. `IChatSessionServices`,
  `IAcpAgentConnection`) must honor the same contract for every caller — no implementation may
  narrow guarantees the interface promises (nullability, exception behavior, thread affinity).
- **Interface Segregation**: don't grow a shared interface to serve one caller's edge case; add a
  narrower interface or an optional capability check instead.
- **Dependency Inversion**: production code depends on the interfaces in `ClaudeCode.Contracts`,
  never directly on a concrete VS SDK/WPF/process type where an existing abstraction already
  exists. New cross-project contracts belong in `ClaudeCode.Contracts` (netstandard2.0, referenced
  by everything) so they stay reachable from every project in the solution.

## Occam's razor — the smallest correct fix wins

- Implement exactly what the defect requires. No new abstraction layers, no new configuration
  surface, no "while I'm here" refactors, no speculative generality for requirements nobody asked
  for.
- When a finding or issue offers both a "minimal" and a "eventual/ideal" fix, take the minimal one
  unless the ask explicitly requires the larger change. Note the deferred option in the PR
  description so it isn't silently lost.
- Prefer fixing the actual defect over adding a parallel safety net around it. Don't add a new
  dependency, package, or project to solve a problem the existing toolchain already solves.
- A fix that touches five files when one file plus an existing seam would do is the wrong fix,
  even if it "future-proofs" something.

## Conventional Commits

Every commit message follows [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<optional scope>): <short summary, imperative mood, no trailing period>

<optional body: what changed and why, wrapped at ~72 columns>

<optional footer: BREAKING CHANGE:, Fixes #123, Refs #123>
```

Types used in this repo: `feat`, `fix`, `security`, `perf`, `refactor`, `test`, `docs`, `build`,
`ci`, `chore`. Scope is the project or area touched when it disambiguates, e.g.
`fix(vscontrol): validate paths against workspace root`, `security(acp): reject fs paths outside
workspace`, `ci: pin release action to a commit SHA`.

Rules:

- One logical change per commit; a commit that mixes an unrelated formatting sweep with a bug fix
  is two commits.
- `security:` (not `fix:`) for commits closing an authorization/injection/DoS-class gap, so the
  history is greppable for security-relevant changes independent of severity labels elsewhere.
- Breaking changes to a public contract (anything in `ClaudeCode.Contracts`, or a signature change
  on an interface implemented outside the owning project) get a `BREAKING CHANGE:` footer even if
  the type is `fix`.
- Reference the finding/issue id in the footer when the commit closes a tracked item.

## Project-specific conventions (see also `README.md`)

- **Threading**: code that touches DTE/`IVs*`/COM must run on the UI thread via
  `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()`; never `.Result`/`.Wait()`/
  `.GetAwaiter().GetResult()` on the UI thread, never `async void` outside a WPF/COM event handler
  boundary (and even there, wrap the body in try/catch — an uncaught exception in a WPF event
  handler crashes the whole `devenv.exe` process).
- **Untrusted input boundary**: anything that originates from the ACP agent process, an MCP tool
  call, or a file in the open workspace is untrusted. Path parameters from that boundary MUST be
  validated with `ClaudeCode.Contracts.WorkspacePathGuard.TryResolveWithinWorkspace` before any
  filesystem or VS API access — never trust a path forwarded from the wire.
- **Resource lifetime**: every subscribed event, spawned process, and `CancellationTokenSource`
  needs a matching unsubscribe/kill/dispose on every exit path (success, exception, cancellation).
  Pair them visibly — subscribe and unsubscribe in the same review-sized diff.
- **`.editorconfig` is enforced, not decorative** — keep it free of rules for analyzer packages the
  solution doesn't reference; a dead rule is worse than no rule because it implies false coverage.

## Validation before yielding

- Run only the test project(s) for the code you changed during the TDD loop; a full-solution
  build/test pass is the final gate before a PR, not something to run after every edit.
- Never run formatters/linters as a substitute for actually satisfying `.editorconfig` — fix the
  code, don't launder it through a tool.
- A PR is not done until: the specific finding/bug is fixed, a test proves it (or the untestable
  exception above is documented), existing tests still pass, and no unrelated file was touched.
