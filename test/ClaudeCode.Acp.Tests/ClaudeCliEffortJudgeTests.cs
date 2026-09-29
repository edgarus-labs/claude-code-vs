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
        fs.writeFileSync(log + '.pid', String(process.pid));
        // Modes that never read stdin: the CLI failing at startup, or stalling before it reads.
        if (mode === 'fail-early') { process.stderr.write('Not logged in · Please run /login'); process.exit(1); }
        if (mode === 'stall') { setInterval(() => {}, 1000); return; }
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
            case 'fail-silent': process.exit(1);
            case 'oversize': process.stdout.write('x'.repeat(20000)); break;
            case 'flood':
              process.stderr.write('e'.repeat(100000), () => process.stdout.write('o'.repeat(20000), () => process.exit(1)));
              break;
            case 'hang': setInterval(() => {}, 1000); break;
          }
        });
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "effort-judge-" + Guid.NewGuid().ToString("N"));
    private readonly string _script;
    private readonly string _log;
    private string _pidFile => _log + ".pid";

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
        Assert.Equal(EffortJudgePrompt.SystemPrompt, args[Array.IndexOf(args, "--system-prompt") + 1]);
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
        var systemPrompts = calls.Select(call => call.Args[Array.IndexOf(call.Args, "--system-prompt") + 1]).ToArray();
        Assert.Equal(EffortJudgePrompt.SystemPrompt, systemPrompts[0]);
        Assert.Equal(EffortJudgePrompt.RetrySystemPrompt, systemPrompts[1]);
        // A retry that repeated the first prompt verbatim would just repeat the first answer.
        Assert.NotEqual(systemPrompts[0], systemPrompts[1]);
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
        await AssertChildGone();
    }

    // RenderUser bounds the state to 2,000 chars, so through the public API the stdin payload stays
    // near 4 KB and does not fill a Linux (64 KB) pipe, at most Windows' small one; the blocked-write
    // path in RunAsync is therefore not reliably reachable here. This pins that a CLI which exits or
    // stalls before reading stdin surfaces its own error, or the timeout, never a broken pipe or a hang.
    private static readonly string OversizedMessage = new string('ż', 5000);

    [Fact]
    public async Task Classify_CliExitsBeforeReadingStdin_ReportsItsErrorNotABrokenPipe()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Judge("fail-early").ClassifyAsync(OversizedMessage, CancellationToken.None));

        Assert.Contains("Not logged in", error.Message);
    }

    [Fact]
    public async Task Classify_CliNeverReadsStdin_TimesOut_AndTheChildIsGone()
    {
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => Judge("stall", TimeSpan.FromSeconds(2)).ClassifyAsync(OversizedMessage, CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        await AssertChildGone();
    }

    [Fact]
    public async Task Classify_CallerCancels_TheChildIsGone()
    {
        using var cancel = new CancellationTokenSource();
        var classifying = Judge("hang").ClassifyAsync("ok", cancel.Token);
        await WaitForPidFile();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => classifying);
        await AssertChildGone();
    }

    // F-50-33: the 15 s bound covers the whole judgment. Resolving the adapter probes the filesystem
    // and cannot observe the token, so a probe stuck on an unreachable path must not hold the turn
    // past the deadline.
    [Fact]
    public async Task Classify_AdapterResolutionThatIgnoresTheDeadline_StillTimesOut()
    {
        var neverResolves = new TaskCompletionSource<AcpExecutableSpec>();
        var judge = new ClaudeCliEffortJudge(_ => neverResolves.Task, TimeSpan.FromMilliseconds(200));

        var classifying = judge.ClassifyAsync("ok", CancellationToken.None);
        var finished = await Task.WhenAny(classifying, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(classifying, finished);
        await Assert.ThrowsAsync<TimeoutException>(() => classifying);
    }

    // F-50-33: caller cancellation is honoured while the adapter is being resolved too.
    [Fact]
    public async Task Classify_CallerCancelsWhileTheAdapterIsBeingResolved_EndsAtOnce()
    {
        var neverResolves = new TaskCompletionSource<AcpExecutableSpec>();
        var judge = new ClaudeCliEffortJudge(_ => neverResolves.Task);
        using var cancel = new CancellationTokenSource();

        var classifying = judge.ClassifyAsync("ok", cancel.Token);
        cancel.Cancel();
        var finished = await Task.WhenAny(classifying, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(classifying, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => classifying);
    }

    // F-50-42: a deadline that expires before the child has written its pid leaves nothing to
    // check; it must not read as a failure of the judge.
    [Fact]
    public async Task Classify_DeadlineExpiresBeforeTheChildStarts_TimesOutAndLeavesNoChild()
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => Judge("stall", TimeSpan.FromMilliseconds(1)).ClassifyAsync("ok", CancellationToken.None));

        await AssertChildGone();
    }

    private int? ReadPid() =>
        File.Exists(_pidFile) && int.TryParse(File.ReadAllText(_pidFile), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pid) ? pid : null;

    private async Task WaitForPidFile()
    {
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(File.Exists(_pidFile) && File.ReadAllText(_pidFile).Length > 0))
        {
            Assert.True(DateTime.UtcNow < giveUp, "the fake adapter never started");
            await Task.Delay(20);
        }
    }

    // The kill/dispose has run by the time ClassifyAsync throws, but the OS may take a moment. A
    // child killed before it wrote its pid has, by then, nothing left to outlive the judgment.
    private async Task AssertChildGone()
    {
        if (ReadPid() is not { } pid) return;
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (IsAlive(pid))
        {
            Assert.True(DateTime.UtcNow < giveUp, $"child {pid} is still running");
            await Task.Delay(50);
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Output far beyond the reply/error bounds while both pipes are open: the judge must keep
    // draining (no deadlock on a full pipe), finish long before its deadline and report bounded text.
    [Fact]
    public async Task Classify_CliFloodsBothPipesAndFails_FinishesWithBoundedError()
    {
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Judge("flood").ClassifyAsync("ok", CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.True(error.Message.Length < 1000, error.Message.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Over the reply bound the reader keeps nothing, so the reply is indistinguishable from an
    // empty one: retrying would only repeat it, and the error must say why.
    [Fact]
    public async Task Classify_ReplyOverTheLimit_FailsOnceAndSaysSo()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Judge("oversize").ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Calls());
    }

    [Fact]
    public async Task Classify_CliFailsWithoutAnyOutput_ReportsTheExitCodeWithoutATrailingColon()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Judge("fail-silent").ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains("code 1", error.Message);
        Assert.Equal(error.Message.TrimEnd(), error.Message);
        Assert.False(error.Message.EndsWith(':'), error.Message);
    }

    [Fact]
    public async Task Classify_AdapterCannotStart_SurfacesTheStartFailure()
    {
        var judge = new ClaudeCliEffortJudge(_ => Task.FromResult(new AcpExecutableSpec(Path.Combine(_directory, "no-such-adapter"), Array.Empty<string>())));

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => judge.ClassifyAsync("ok", CancellationToken.None));
    }
}
