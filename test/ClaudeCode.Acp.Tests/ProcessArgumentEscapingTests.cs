using System;
using System.Runtime.InteropServices;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// Covers <c>ProcessArgumentEscaping.ToArgumentsString</c> by parsing its output with the Win32
/// <c>CommandLineToArgvW</c> parser and comparing the result with the original arguments.
/// </summary>
public sealed class ProcessArgumentEscapingTests
{
    [Theory]
    [InlineData("simple")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("trailing\\")]
    [InlineData("trailing\\\\")]
    [InlineData("quote\"inside")]
    [InlineData("back\\\"slash-quote")]
    [InlineData("back\\\\\\\"triple")]
    [InlineData(" leading and trailing ")]
    [InlineData("\"\"\"")]
    [InlineData("C:\\Program Files\\App\\")]
    [InlineData("C:\\Program Files (x86)\\App")]
    [InlineData("&calc.exe")]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("a\tb")]
    [InlineData("only\\backslashes\\\\")]
    [InlineData("mix\\\\\\path\\")]
    [InlineData("value\" --evil-flag \"injected")]
    [InlineData("--looks-like-a-flag")]
    public void ToArgumentsString_SingleArgument_RoundTripsThroughRealWindowsArgvParser(string argument)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string commandLine = "\"C:\\dummy.exe\" " + ProcessArgumentEscaping.ToArgumentsString(new[] { argument });

        Assert.Equal(new[] { "C:\\dummy.exe", argument }, ParseViaWindowsApi(commandLine));
    }

    [Fact]
    public void ToArgumentsString_MultipleArguments_PreservesBoundariesOrderAndEmptyEntries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string[] arguments = { "first", "second with spaces", "", "trailing\\", "quote\"here", "value\" --evil \"end", "-flag" };
        string commandLine = "\"C:\\dummy.exe\" " + ProcessArgumentEscaping.ToArgumentsString(arguments);

        string[] parsed = ParseViaWindowsApi(commandLine);
        Assert.Equal(arguments.Length + 1, parsed.Length);
        Assert.Equal(arguments, parsed[1..]);
    }

    [Fact]
    public void ToArgumentsString_NoWhitespaceOrQuotes_IsNotQuoted() => Assert.Equal("plain-arg", ProcessArgumentEscaping.ToArgumentsString(new[] { "plain-arg" }));

    [Fact]
    public void ToArgumentsString_EmptyArgumentList_ProducesEmptyString() => Assert.Equal(string.Empty, ProcessArgumentEscaping.ToArgumentsString(Array.Empty<string>()));

    private static string[] ParseViaWindowsApi(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                IntPtr strPtr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringUni(strPtr)!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
