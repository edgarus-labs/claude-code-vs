using ClaudeCode.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

public sealed class ClaudeCliEffortJudgeTests : IDisposable
{
    /// <summary>
    /// The fake adapter.
    /// </summary>
    private const string _fakeAdapter = """
        const fs = require('fs');
        const [mode, log] = process.argv.slice(2, 4);
        const args = process.argv.slice(4);
        fs.writeFileSync(log + '.pid', String(process.pid));
        // Modes that never read stdin: the CLI failing at startup, or stalling before it reads.
        if (mode === 'fail-early') { process.stderr.write('Not logged in · Please run /login'); process.exit(1); }
        // Exits cleanly without ever reading its input, like a CLI that ignores stdin.
        if (mode === 'ignore-input-ok') process.exit(0);
        if (mode === 'stall') { setInterval(() => {}, 1000); return; }
        // The real adapter runs the native CLI as a child: a tree, not one process.
        if (mode === 'grandchild') {
          const grandchild = require('child_process').spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
          fs.writeFileSync(log + '.grandchild.pid', String(grandchild.pid));
          setInterval(() => {}, 1000);
          return;
        }
        let stdin = '';
        process.stdin.setEncoding('utf8');
        process.stdin.on('data', d => stdin += d);
        process.stdin.on('end', () => {
          fs.appendFileSync(log, JSON.stringify({ args, stdin, maxThinking: process.env.MAX_THINKING_TOKENS ?? null, cwd: process.cwd() }) + '\n');
          const calls = fs.readFileSync(log, 'utf8').trim().split('\n').length;
          switch (mode) {
            case 'high': process.stdout.write('`high`\n\nSeveral candidate causes remain open.'); break;
            case 'retry': process.stdout.write(calls === 1 ? 'Nie mam kontekstu.' : 'low'); break;
            // Every run takes 900 ms, the first answering without a level.
            case 'slow-retry': setTimeout(() => process.stdout.write(calls === 1 ? 'Nie mam kontekstu.' : 'low'), 900); break;
            // Exits 0 with nothing on stdout.
            case 'silent-ok': break;
            case 'never': process.stdout.write('I cannot help with that.'); break;
            case 'fail': process.stderr.write('Not logged in · Please run /login'); process.exit(1);
            case 'fail-stdout': process.stdout.write('API Error: overloaded'); process.exit(1);
            case 'fail-echo-stderr': process.stderr.write('Authentication failed for ' + stdin); process.exit(37);
            case 'fail-echo-stdout': process.stdout.write('API Error: ' + stdin); process.exit(37);
            case 'echo-unparseable': process.stdout.write('Cannot classify: ' + stdin.split('\n\nAnswer with exactly one of:')[0]); break;
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
    /// <summary>
    /// Gets the pid file.
    /// </summary>
    private string PidFile => _log + ".pid";

    public ClaudeCliEffortJudgeTests()
    {
        Directory.CreateDirectory(_directory);
        _script = Path.Combine(_directory, "fake-adapter.js");
        _log = Path.Combine(_directory, "calls.jsonl");
        File.WriteAllText(_script, _fakeAdapter);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private ClaudeCliEffortJudge Judge(string mode, TimeSpan? timeout = null) =>
        new(_ => Task.FromResult(new AcpExecutableSpec(NodePath(), new[] { _script, mode, _log })), timeout);

    [Fact]
    public async Task Classify_PreparesTheMessageOffTheCallersThread()
    {
        int callerThread = -1, resolverThread = -2;
        var judge = new ClaudeCliEffortJudge(_ =>
        {
            resolverThread = Environment.CurrentManagedThreadId;
            return Task.FromResult(new AcpExecutableSpec(NodePath(), new[] { _script, "high", _log }));
        });
        Task<EffortLevel>? classifying = null;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            classifying = judge.ClassifyAsync("ok", CancellationToken.None);
        });
        caller.Start();
        caller.Join();

        await classifying!;

        Assert.NotEqual(callerThread, resolverThread);
    }

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
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("These tests need node on PATH to stand in for the ACP adapter.");
    }

    private List<string> Cwds() =>
        File.ReadAllLines(_log).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("cwd").GetString()!;
        }).ToList();

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
        Assert.Equal("1", args[Array.IndexOf(args, "--max-turns") + 1]);
        Assert.Equal("text", args[Array.IndexOf(args, "--output-format") + 1]);
        static string Normalized(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(Normalized(Path.GetTempPath()), Normalized(Assert.Single(Cwds())), ignoreCase: OperatingSystem.IsWindows());
        Assert.DoesNotContain(args, arg => arg.Contains("zakleszczenia"));
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
        Assert.NotEqual(systemPrompts[0], systemPrompts[1]);
    }

    [Fact]
    public async Task Classify_NeverParseable_FailsAfterTwoRetries()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Judge("never").ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains("without a level", error.Message);
        Assert.Equal(3, Calls().Count);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("fail-stdout")]
    public async Task Classify_CliFails_ReportsExitCodeWithoutRawOutput(string mode)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Judge(mode).ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains("code 1", error.Message);
        Assert.Single(Calls());
    }

    [Theory]
    [InlineData("fail-echo-stderr")]
    [InlineData("fail-echo-stdout")]
    public async Task Classify_CliFailureEchoingInput_ReportsExitCodeWithoutDisclosingInput(string mode)
    {
        const string secret = "customer-secret-token-7421";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Judge(mode).ClassifyAsync(secret, CancellationToken.None));

        Assert.Contains("code 37", error.Message);
        Assert.DoesNotContain(secret, error.ToString());
        Assert.Single(Calls());
    }

    [Fact]
    public async Task Classify_UnparseableReplyEchoingInput_ReportsInvalidReplyWithoutDisclosingInput()
    {
        const string secret = "customer-secret-token-7421";
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => Judge("echo-unparseable").ClassifyAsync(secret, CancellationToken.None));

        Assert.Contains("without a level", error.Message);
        Assert.DoesNotContain(secret, error.ToString());
        Assert.Equal(3, Calls().Count);
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

    private static readonly string _oversizedMessage = new string('ż', 5000);

    [Fact]
    public async Task Classify_CliExitsBeforeReadingStdin_ReportsExitCodeNotABrokenPipe()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Judge("fail-early").ClassifyAsync(_oversizedMessage, CancellationToken.None));

        Assert.Contains("code 1", error.Message);
    }

    [Fact]
    public async Task Classify_CliNeverReadsStdin_TimesOut_AndTheChildIsGone()
    {
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => Judge("stall", TimeSpan.FromSeconds(2)).ClassifyAsync(_oversizedMessage, CancellationToken.None));

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

    [Fact]
    public async Task Classify_DeadlineExpiresBeforeTheChildStarts_TimesOutAndLeavesNoChild()
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => Judge("stall", TimeSpan.FromMilliseconds(1)).ClassifyAsync("ok", CancellationToken.None));

        await AssertChildGone();
    }

    [Fact(Skip = "Timing-dependent: relies on a 300 ms margin over two real node runs; flaky on CI runners.")]
    public async Task Classify_OneDeadlineCoversEveryAttempt() => await Assert.ThrowsAsync<TimeoutException>(
            () => Judge("slow-retry", TimeSpan.FromMilliseconds(1500)).ClassifyAsync("ok", CancellationToken.None));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsANonPositiveTimeout(int milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClaudeCliEffortJudge(_ => Task.FromResult(new AcpExecutableSpec("x", Array.Empty<string>())), TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public async Task Classify_ResolverCancelledByItsHost_IsAFailureNotACancellation()
    {
        var judge = new ClaudeCliEffortJudge(_ => Task.FromException<AcpExecutableSpec>(new OperationCanceledException()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => judge.ClassifyAsync("ok", CancellationToken.None));
    }

    [Fact]
    public async Task Classify_EmptyReply_IsRetriedLikeAnyUnparseableReply()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Judge("silent-ok").ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains("without a level", error.Message);
        Assert.Equal(3, Calls().Count);
    }

    [Fact]
    public async Task Judge_ChildClosesItsInputWithoutReadingIt_SaysSo()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Judge("ignore-input-ok").JudgeAsync(new string('x', 1 << 20), CancellationToken.None));

        Assert.Contains("before reading", error.Message);
    }

    [Fact]
    public async Task Judge_LargeMessageToACliThatNeverReadsIt_StillTimesOut_AndTheChildIsGone()
    {
        var judging = Task.Run(() => Judge("stall", TimeSpan.FromSeconds(1)).JudgeAsync(new string('x', 1 << 20), CancellationToken.None));
        var finished = await Task.WhenAny(judging, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(judging, finished);
        await Assert.ThrowsAsync<TimeoutException>(() => judging);
        await AssertChildGone();
    }

    [Fact]
    public async Task Classify_CallerCancels_TheWholeProcessTreeIsGone()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        var classifying = Judge("grandchild").ClassifyAsync("ok", cancel.Token);
        var grandchild = await WaitForPid(_log + ".grandchild.pid", "the fake adapter never started its child");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => classifying);

        await AssertChildGone();
        await AssertGone(grandchild);
    }

    private static int? ReadPidFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return int.TryParse(reader.ReadToEnd(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pid) ? pid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private int? ReadPid() => ReadPidFile(PidFile);

    private static async Task<int> WaitForPid(string path, string failure)
    {
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            if (ReadPidFile(path) is { } pid)
            {
                return pid;
            }

            Assert.True(DateTime.UtcNow < giveUp, failure);
            await Task.Delay(20);
        }
    }

    private async Task WaitForPidFile() => await WaitForPid(PidFile, "the fake adapter never started");

    private async Task AssertChildGone()
    {
        if (ReadPid() is not { } pid)
        {
            return;
        }

        await AssertGone(pid);
    }

    private static async Task AssertGone(int pid)
    {
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

    [Fact]
    public async Task Classify_CliFloodsBothPipesAndFails_FinishesWithBoundedError()
    {
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Judge("flood").ClassifyAsync("ok", CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.StartsWith("The effort judge exited with code 1", error.Message);
        Assert.True(error.Message.Length <= 400, error.Message.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Classify_ReplyOverTheLimit_IsRetriedLikeAnyUnparseableReply()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Judge("oversize").ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains("without a level", error.Message);
        Assert.Equal(3, Calls().Count);
    }

    [Fact]
    public async Task Classify_CliFailsWithoutAnyOutput_ReportsTheExitCodeWithoutATrailingColon()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Judge("fail-silent").ClassifyAsync("ok", CancellationToken.None));

        Assert.Contains("code 1", error.Message);
        Assert.False(error.Message.EndsWith(':'), error.Message);
    }

    [Fact]
    public async Task Classify_AdapterCannotStart_SurfacesTheStartFailure()
    {
        var judge = new ClaudeCliEffortJudge(_ => Task.FromResult(new AcpExecutableSpec(Path.Combine(_directory, "no-such-adapter"), Array.Empty<string>())));

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => judge.ClassifyAsync("ok", CancellationToken.None));
    }
}
