using ClaudeCode.Acp;
using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

// Drives the real process path against a fake adapter: a node script standing in for
// `claude-agent-acp --cli -p`, which records what it was asked and answers per scenario.
public sealed class ClaudeCliEffortJudgeTests : IDisposable
{
    private const string FakeAdapter = """
        const fs = require('fs');
        const [mode, log] = process.argv.slice(2, 4);
        const args = process.argv.slice(4);
        let stdin = '';
        process.stdin.setEncoding('utf8');
        process.stdin.on('data', d => stdin += d);
        process.stdin.on('end', () => {
          fs.appendFileSync(log, JSON.stringify({ args, stdin, maxThinking: process.env.MAX_THINKING_TOKENS ?? null }) + '\n');
          const calls = fs.readFileSync(log, 'utf8').trim().split('\n').length;
          switch (mode) {
            case 'high': process.stdout.write('`high`\n\nSeveral candidate causes remain open.'); break;
            case 'retry': process.stdout.write(calls === 1 ? 'Nie mam kontekstu.' : 'low'); break;
            case 'never': process.stdout.write('I cannot help with that.'); break;
            case 'fail': process.stderr.write('Not logged in · Please run /login'); process.exit(1);
            case 'fail-stdout': process.stdout.write('API Error: overloaded'); process.exit(1);
            case 'hang': setInterval(() => {}, 1000); break;
          }
        });
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "effort-judge-" + Guid.NewGuid().ToString("N"));
    private readonly string _script;
    private readonly string _log;

    public ClaudeCliEffortJudgeTests()
    {
        Directory.CreateDirectory(_directory);
        _script = Path.Combine(_directory, "fake-adapter.js");
        _log = Path.Combine(_directory, "calls.jsonl");
        File.WriteAllText(_script, FakeAdapter);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private ClaudeCliEffortJudge Judge(string mode, TimeSpan? timeout = null) =>
        new(_ => Task.FromResult(new AcpExecutableSpec(NodePath(), new[] { _script, mode, _log })), timeout);

    private List<(string[] Args, string Stdin, string? MaxThinking)> Calls() =>
        File.ReadAllLines(_log).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var args = root.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray();
            return (args, root.GetProperty("stdin").GetString()!, root.GetProperty("maxThinking").GetString());
        }).ToList();

    private static string NodePath()
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .First(File.Exists);
    }

    [Fact]
    public async Task Classify_AsksTheBundledCliOnceAsAToolLessHaikuJudge_WithTheStateOnStdin()
    {
        var level = await Judge("high").ClassifyAsync("znajdź przyczynę zakleszczenia między workerami", CancellationToken.None);

        Assert.Equal(EffortLevel.High, level);
        var (args, stdin, maxThinking) = Assert.Single(Calls());
        Assert.Equal(new[] { "--cli", "-p", "--model", "haiku" }, args.Take(4));
        Assert.Equal(EffortJudgePrompt.System, args[Array.IndexOf(args, "--system-prompt") + 1]);
        Assert.Equal("", args[Array.IndexOf(args, "--tools") + 1]);
        Assert.Equal("", args[Array.IndexOf(args, "--setting-sources") + 1]);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("--strict-mcp-config", args);
        // The untrusted message never reaches the command line, and survives as UTF-8.
        Assert.DoesNotContain(args, arg => arg.Contains("zakleszczenia"));
        // Reasoning off, as oh-my-pi's judge: with thinking a single judgment took 22-42 s, without ~4 s.
        Assert.Equal("0", maxThinking);
        Assert.Equal(EffortJudgePrompt.RenderUser("znajdź przyczynę zakleszczenia między workerami"), stdin);
    }

    [Fact]
    public async Task Classify_UnparseableReply_RetriesWithTheRetrySystemPrompt()
    {
        var level = await Judge("retry").ClassifyAsync("zrób commit i push", CancellationToken.None);

        Assert.Equal(EffortLevel.Low, level);
        var calls = Calls();
        Assert.Equal(2, calls.Count);
        Assert.Equal(EffortJudgePrompt.RetrySystem, calls[1].Args[Array.IndexOf(calls[1].Args, "--system-prompt") + 1]);
    }

    [Fact]
    public async Task Classify_NeverParseable_FailsAfterTwoRetries()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Judge("never").ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains("I cannot help with that.", error.Message);
        Assert.Equal(3, Calls().Count);
    }

    // The CLI reports some failures (API errors) on stdout with nothing on stderr.
    [Theory]
    [InlineData("fail", "Not logged in")]
    [InlineData("fail-stdout", "API Error: overloaded")]
    public async Task Classify_CliFails_ReportsItsError(string mode, string expected)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Judge(mode).ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains(expected, error.Message);
        Assert.Single(Calls());
    }

    [Fact]
    public async Task Classify_NoAnswerInTime_TimesOut()
    {
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => Judge("hang", TimeSpan.FromSeconds(2)).ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains("2 s", error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task Classify_CallerCancels_IsCancellationNotTimeout()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Judge("hang").ClassifyAsync("ok", cancel.Token));
    }
}
