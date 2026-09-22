using ClaudeCode.Contracts;
using System.Globalization;

namespace ClaudeCode.Acp;

/// <summary>Maps the result of the adapter CLI's interactive login/logout - its exit code and the
/// status probe that follows it - to the message the user sees. Kept free of process and VS code so
/// every branch is unit-testable.</summary>
public static class AcpAuthCommandOutcomes
{
    public const string LoginInstructions = "Type /login to try again, run 'claude auth login' in a terminal, or "
        + "'claude-agent-acp --cli auth login' to use the adapter's bundled CLI, then check sign-in again. "
        + "Use the same CLAUDE_CONFIG_DIR environment as Visual Studio and restart Visual Studio after changing it.";

    public const string AdapterMissingMessage = "Install @agentclientprotocol/claude-agent-acp and Node.js 22 or newer, "
        + "or configure its ACP executable path in Tools > Options > Claude Code.";

    private const string LogoutInstructions = "Type /logout to try again, or run 'claude-agent-acp --cli auth logout' in a terminal.";

    public const string SignedOutMessage = "Signed out of Claude Code. This affects the CLI, VS Code and other clients on this machine, not only Visual Studio.";

    public static AuthCommandOutcome AdapterMissing { get; } = new AuthCommandOutcome(false, AdapterMissingMessage);

    public static AuthCommandOutcome LoginCouldNotStart { get; } =
        new AuthCommandOutcome(false, "The sign-in console could not be started. " + AdapterMissingMessage);

    public static AuthCommandOutcome LogoutCouldNotStart { get; } =
        new AuthCommandOutcome(false, "The sign-out command could not be started. " + AdapterMissingMessage);

    public static AuthCommandOutcome LogoutTimedOut { get; } =
        new AuthCommandOutcome(false, "Sign-out did not finish in time. " + LogoutInstructions);

    /// <summary>The status probe, not the exit code, decides success: a console the user closed early
    /// can still exit 0 without completing OAuth.</summary>
    public static AuthCommandOutcome ForLogin(AuthState stateAfterProbe, int exitCode) => stateAfterProbe switch
    {
        AuthState.SignedIn => new AuthCommandOutcome(true, "Signed in to Claude."),
        AuthState.SignedOut when exitCode == 0 =>
            new AuthCommandOutcome(false, "The sign-in console finished, but native sign-in could not be confirmed. " + LoginInstructions),
        AuthState.SignedOut =>
            new AuthCommandOutcome(false, $"Sign-in was not completed (exit code {Format(exitCode)}). " + LoginInstructions),
        _ => new AuthCommandOutcome(false, $"The sign-in console finished (exit code {Format(exitCode)}), but the sign-in status check "
            + "was inconclusive. Run 'claude-agent-acp --cli auth status --json' in a terminal to check. " + LoginInstructions),
    };

    /// <summary>A zero exit only counts once the status probe agrees: credentials from an API key or a
    /// third-party provider in the environment survive <c>auth logout</c>.</summary>
    public static AuthCommandOutcome ForLogout(int exitCode, AuthState stateAfterProbe)
    {
        if (exitCode != 0)
        {
            return new AuthCommandOutcome(false, $"Sign-out did not complete (exit code {Format(exitCode)}). " + LogoutInstructions);
        }

        return stateAfterProbe == AuthState.SignedIn
            ? new AuthCommandOutcome(false, "The Claude account was signed out, but Claude Code still reports authentication, "
                + "for example an API key or a third-party provider configured in the environment. Remove that configuration to sign out fully.")
            : new AuthCommandOutcome(true, SignedOutMessage);
    }

    private static string Format(int exitCode) => exitCode.ToString(CultureInfo.InvariantCulture);
}
