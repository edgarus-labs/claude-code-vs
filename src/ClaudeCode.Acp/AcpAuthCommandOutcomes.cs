using ClaudeCode.Contracts;
using System.Globalization;

namespace ClaudeCode.Acp;

/// <summary>Maps the exit code and follow-up status probe of the adapter CLI's interactive login/logout
/// to the message the user sees.</summary>
public static class AcpAuthCommandOutcomes
{
    /// <summary>
    /// The login instructions.
    /// </summary>
    public const string LoginInstructions = "Type /login to try again, run 'claude auth login' in a terminal, or "
        + "'claude-agent-acp --cli auth login' to use the adapter's bundled CLI, then check sign-in again. "
        + "Use the same CLAUDE_CONFIG_DIR environment as Visual Studio and restart Visual Studio after changing it.";

    /// <summary>
    /// The adapter missing message.
    /// </summary>
    public const string AdapterMissingMessage = "Install @agentclientprotocol/claude-agent-acp and Node.js 22 or newer, "
        + "or configure its ACP executable path in Tools > Options > Claude Code.";

    /// <summary>
    /// The logout instructions.
    /// </summary>
    private const string _logoutInstructions = "Type /logout to try again, or run 'claude-agent-acp --cli auth logout' in a terminal.";

    /// <summary>
    /// The signed out message.
    /// </summary>
    public const string SignedOutMessage = "Signed out of Claude Code. This affects the CLI, VS Code and other clients on this machine, not only Visual Studio.";

    /// <summary>
    /// Gets the adapter missing.
    /// </summary>
    public static AuthCommandOutcome AdapterMissing { get; } = new AuthCommandOutcome(false, AdapterMissingMessage);

    /// <summary>
    /// Gets the login could not start.
    /// </summary>
    public static AuthCommandOutcome LoginCouldNotStart { get; } =
        new AuthCommandOutcome(false, "The sign-in console could not be started. " + AdapterMissingMessage);

    /// <summary>
    /// Gets the logout could not start.
    /// </summary>
    public static AuthCommandOutcome LogoutCouldNotStart { get; } =
        new AuthCommandOutcome(false, "The sign-out command could not be started. " + AdapterMissingMessage);

    /// <summary>
    /// Gets the logout timed out.
    /// </summary>
    public static AuthCommandOutcome LogoutTimedOut { get; } =
        new AuthCommandOutcome(false, "Sign-out did not finish in time. " + _logoutInstructions);

    /// <summary>Returns the login outcome; success is decided by <paramref name="stateAfterProbe"/>, not by
    /// <paramref name="exitCode"/>.</summary>
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

    /// <summary>Returns the logout outcome; succeeds only when <paramref name="exitCode"/> is zero and the
    /// status probe agrees.</summary>
    public static AuthCommandOutcome ForLogout(int exitCode, AuthState stateAfterProbe)
    {
        if (exitCode != 0)
        {
            return new AuthCommandOutcome(false, $"Sign-out did not complete (exit code {Format(exitCode)}). " + _logoutInstructions);
        }

        return stateAfterProbe == AuthState.SignedIn
            ? new AuthCommandOutcome(false, "The Claude account was signed out, but Claude Code still reports authentication, "
                + "for example an API key or a third-party provider configured in the environment. Remove that configuration to sign out fully.")
            : new AuthCommandOutcome(true, SignedOutMessage);
    }

    /// <summary>
    /// Formats the specified exit code as an invariant culture string.
    /// </summary>
    /// <param name="exitCode">The exit code.</param>
    /// <returns>The string result.</returns>
    private static string Format(int exitCode) => exitCode.ToString(CultureInfo.InvariantCulture);
}
