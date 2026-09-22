using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using System;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class AcpAuthCommandOutcomesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Login_ConfirmedByTheStatusProbe_Succeeds_WhateverTheExitCode(int exitCode)
    {
        var outcome = AcpAuthCommandOutcomes.ForLogin(AuthState.SignedIn, exitCode);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public void Login_ConsoleClosedEarly_ExitZeroButSignedOut_IsAFailure()
    {
        var outcome = AcpAuthCommandOutcomes.ForLogin(AuthState.SignedOut, 0);

        Assert.False(outcome.Succeeded);
        Assert.Contains("could not be confirmed", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Login_NonZeroExitAndSignedOut_ReportsTheExitCode()
    {
        var outcome = AcpAuthCommandOutcomes.ForLogin(AuthState.SignedOut, 3);

        Assert.False(outcome.Succeeded);
        Assert.Contains("exit code 3", outcome.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AuthState.Unknown)]
    [InlineData(AuthState.Error)]
    public void Login_InconclusiveStatusProbe_BlamesTheStatusCheck_NotTheSignIn(AuthState state)
    {
        var outcome = AcpAuthCommandOutcomes.ForLogin(state, 0);

        Assert.False(outcome.Succeeded);
        Assert.Contains("status check", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not completed", outcome.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AuthState.SignedOut)]
    [InlineData(AuthState.Unknown)]
    public void Logout_ExitZero_AndNoLongerSignedIn_Succeeds(AuthState state)
    {
        var outcome = AcpAuthCommandOutcomes.ForLogout(0, state);

        Assert.True(outcome.Succeeded);
        Assert.Contains("Signed out", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Logout_ExitZero_ButStillSignedIn_IsAFailure_NamingTheRemainingCredentialSource()
    {
        var outcome = AcpAuthCommandOutcomes.ForLogout(0, AuthState.SignedIn);

        Assert.False(outcome.Succeeded);
        Assert.Contains("API key", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Logout_NonZeroExit_ReportsTheExitCode_WithoutSignInAdvice()
    {
        var outcome = AcpAuthCommandOutcomes.ForLogout(2, AuthState.SignedIn);

        Assert.False(outcome.Succeeded);
        Assert.Contains("exit code 2", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("auth login", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureOutcomes_AreNeverSuccesses()
    {
        Assert.False(AcpAuthCommandOutcomes.AdapterMissing.Succeeded);
        Assert.False(AcpAuthCommandOutcomes.LoginCouldNotStart.Succeeded);
        Assert.False(AcpAuthCommandOutcomes.LogoutCouldNotStart.Succeeded);
        Assert.False(AcpAuthCommandOutcomes.LogoutTimedOut.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AuthCommandOutcome_RejectsAnEmptyMessage(string message)
    {
        Assert.Throws<ArgumentException>(() => new AuthCommandOutcome(false, message));
    }
}
