using ClaudeCode.Contracts;
using ClaudeCode.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Core.Tests;

public sealed partial class ChatSessionStateTests
{
    private static readonly string[] AdvertisedEfforts = { "low", "medium", "high", "xhigh", "max" };

    private sealed class FakeEffortClassifier : IEffortClassifier
    {
        public List<string> Prompts { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Func<string, Task<EffortLevel>> Handler { get; set; } = _ => Task.FromResult(EffortLevel.Medium);
        // Set for a judge that, like the real one, ends when its token is cancelled.
        public Func<string, CancellationToken, Task<EffortLevel>>? TokenHandler { get; set; }

        public Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            Tokens.Add(cancellationToken);
            return TokenHandler?.Invoke(prompt, cancellationToken) ?? Handler(prompt);
        }
    }

    // The agent acknowledges every effort change as the new current value, like claude-code-acp.
    private static (RecordingAcpAgentConnection Connection, List<string> Log) AutoConnection(string effort = "medium")
    {
        var log = new List<string>();
        var current = effort;
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options("sonnet", effort, AdvertisedEfforts) };
        connection.ConfigHandler = (_, value, _) =>
        {
            log.Add("effort=" + value);
            current = value;
            return Task.FromResult(Options("sonnet", current, AdvertisedEfforts));
        };
        connection.PromptHandler = content =>
        {
            log.Add("prompt:" + ((ContentBlock.Text)content[0]).Value);
            return Task.CompletedTask;
        };
        return (connection, log);
    }

    private static ChatViewModel CreateWithClassifier(RecordingAcpAgentConnection connection, IEffortClassifier? classifier) =>
        new(new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            EffortClassifier = classifier,
        });

    private static SessionConfigValue Auto(ChatViewModel vm) => vm.AvailableEfforts.Single(value => value.Name == "Auto");

    private static async Task SendTextAsync(ChatViewModel vm, string text)
    {
        vm.InputText = text;
        await vm.SendAsync();
    }

    [Fact]
    public async Task AutoEffort_IsOfferedFirst_AlongsideEveryAdvertisedLevel()
    {
        var (connection, _) = AutoConnection();
        using var vm = CreateWithClassifier(connection, new FakeEffortClassifier());
        await vm.Initialization;

        Assert.Equal(new[] { "Auto", "low", "medium", "high", "xhigh", "max" }, vm.AvailableEfforts.Select(value => value.Name));
        Assert.Equal("medium", vm.SelectedEffort!.Value);
    }

    [Fact]
    public async Task AutoEffort_NotOffered_WithoutClassifierOrWithoutLowMediumHigh()
    {
        var (connection, _) = AutoConnection();
        using var noClassifier = CreateWithClassifier(connection, null);
        await noClassifier.Initialization;
        Assert.DoesNotContain(noClassifier.AvailableEfforts, value => value.Name == "Auto");

        using var noLevels = CreateWithClassifier(
            new RecordingAcpAgentConnection { ConfigOptions = Options("sonnet", "default", "default", "xhigh") },
            new FakeEffortClassifier());
        await noLevels.Initialization;
        Assert.DoesNotContain(noLevels.AvailableEfforts, value => value.Name == "Auto");
    }

    [Fact]
    public async Task SelectingAuto_IsLocal_NeverSendsAutoToTheAgent_AndDoesNotLoadTheClassifier()
    {
        var (connection, _) = AutoConnection();
        var classifier = new FakeEffortClassifier();
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;

        await vm.SelectEffortAsync(Auto(vm));

        Assert.Empty(connection.ConfigChanges);
        Assert.Empty(classifier.Prompts);
        Assert.Same(Auto(vm), vm.SelectedEffort);
        Assert.Equal("Auto", vm.ActiveEffortName);
    }

    [Theory]
    [InlineData(EffortLevel.Low, "low")]
    [InlineData(EffortLevel.High, "high")]
    public async Task AutoTurn_SetsClassifiedEffortBeforeItsPrompt_AndStaysOnAuto(EffortLevel level, string expected)
    {
        var (connection, log) = AutoConnection();
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(level) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "do the thing");

        Assert.Equal(new[] { "do the thing" }, classifier.Prompts);
        Assert.Equal(new[] { "effort=" + expected, "prompt:do the thing" }, log);
        Assert.DoesNotContain(connection.ConfigChanges, change => change.Value == "auto");
        Assert.Same(Auto(vm), vm.SelectedEffort);
    }

    // The picker shows which level Auto is running at, not just "Auto", and the bound labels are
    // told to refresh when the judgment lands. Re-selecting Auto forgets the previous verdict.
    [Fact]
    public async Task AutoTurn_ShowsTheChosenLevelInThePicker()
    {
        var (connection, _) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        Assert.Equal("Auto", vm.ActiveEffortName);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await SendTextAsync(vm, "design the sync protocol");

        Assert.Equal("Auto · high", vm.ActiveEffortName);
        Assert.Equal("Sonnet · Auto · high", vm.ModelEffortLabel);
        Assert.Contains(nameof(ChatViewModel.ActiveEffortName), changed);
        Assert.Contains(nameof(ChatViewModel.ModelEffortLabel), changed);

        await vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "low"));
        await vm.SelectEffortAsync(Auto(vm));
        Assert.Equal("Auto", vm.ActiveEffortName);
    }

    // A fallback level is still the level the turn runs at, so it is shown too.
    [Fact]
    public async Task AutoTurn_JudgmentFails_ShowsTheFallbackLevel()
    {
        var (connection, _) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromException<EffortLevel>(new TimeoutException("slow")) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "hello");

        Assert.Equal("Auto · high", vm.ActiveEffortName);
    }

    [Fact]
    public async Task AutoTurn_EffortAlreadyInPlace_SendsOnlyThePrompt()
    {
        var (connection, log) = AutoConnection("medium");
        using var vm = CreateWithClassifier(connection, new FakeEffortClassifier());
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "refactor this");

        Assert.Equal(new[] { "prompt:refactor this" }, log);
    }

    // Auto's ceiling is High: xhigh/max stay available, but only as an explicit choice.
    [Fact]
    public async Task AutoTurn_NeverSelectsAboveHigh_EvenFromAHigherExplicitLevel()
    {
        var (connection, log) = AutoConnection("max");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "design the sync protocol");

        Assert.Equal(new[] { "effort=high", "prompt:design the sync protocol" }, log);
    }

    // Before any verdict there is nothing better than the agent default Auto starts from: High.
    [Fact]
    public async Task AutoTurn_FirstJudgmentFails_FallsBackToHighAndStillSendsTheTurn()
    {
        var (connection, log) = AutoConnection("low");
        var classifier = new FakeEffortClassifier
        {
            Handler = _ => Task.FromException<EffortLevel>(new TimeoutException("judge timed out")),
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "hello");

        Assert.Equal(new[] { "effort=high", "prompt:hello" }, log);
        Assert.Contains("judge timed out", vm.StatusMessage);
        Assert.Same(Auto(vm), vm.SelectedEffort);
    }

    // After a verdict, a failed judgment keeps the last level Auto chose.
    [Fact]
    public async Task AutoTurn_LaterJudgmentFails_KeepsTheLastJudgedLevel()
    {
        var (connection, log) = AutoConnection("medium");
        var verdicts = new Queue<Task<EffortLevel>>(new[]
        {
            Task.FromResult(EffortLevel.Low),
            Task.FromException<EffortLevel>(new InvalidOperationException("unparseable reply")),
        });
        var classifier = new FakeEffortClassifier { Handler = _ => verdicts.Dequeue() };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "git push");
        await SendTextAsync(vm, "second");

        Assert.Equal(new[] { "effort=low", "prompt:git push", "prompt:second" }, log);
        Assert.Contains("unparseable reply", vm.StatusMessage);
    }

    [Fact]
    public async Task AutoTurn_EffortChangeRejected_KeepsTheDraftAndSendsNothing()
    {
        var (connection, log) = AutoConnection("medium");
        connection.ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"));
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "hard work");

        Assert.Empty(log);
        Assert.Equal("hard work", vm.InputText);
        Assert.Contains("rejected", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ExplicitEffort_AfterAuto_LeavesAuto_AndTurnsNeverConsultTheClassifier()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "xhigh"));
        await SendTextAsync(vm, "git status");

        Assert.Equal(new[] { "effort=xhigh", "prompt:git status" }, log);
        Assert.Empty(classifier.Prompts);
        Assert.Equal("xhigh", vm.SelectedEffort!.Value);
    }

    // Picking the level that is already current still leaves Auto, without a round trip.
    [Fact]
    public async Task ExplicitEffort_EqualToCurrent_LeavesAutoWithoutAConfigRequest()
    {
        var (connection, _) = AutoConnection("medium");
        using var vm = CreateWithClassifier(connection, new FakeEffortClassifier());
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "medium"));

        Assert.Equal("medium", vm.SelectedEffort!.Value);
        Assert.Empty(connection.ConfigChanges);
    }

    [Fact]
    public async Task ExplicitEffort_TurnsBehaveAsBefore_WithoutTouchingTheClassifier()
    {
        var (connection, log) = AutoConnection("high");
        var classifier = new FakeEffortClassifier();
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;

        await SendTextAsync(vm, "first");
        await SendTextAsync(vm, "second");

        Assert.Equal(new[] { "prompt:first", "prompt:second" }, log);
        Assert.Empty(classifier.Prompts);
    }

    // The agent queues prompts, but effort is session-wide: a follow-up sent ahead would either
    // retune the running turn or run under the running turn's effort. Under Auto each follow-up
    // waits for the turn before it, then gets its own effort immediately ahead of its own prompt.
    [Fact]
    public async Task AutoTurns_AreNotSentAhead_EachPromptRunsUnderItsOwnEffort()
    {
        var (connection, log) = AutoConnection("medium");
        connection.SupportsPromptQueueing = true;
        var firstTurn = new TaskCompletionSource();
        connection.PromptHandler = content =>
        {
            var text = ((ContentBlock.Text)content[0]).Value;
            log.Add("prompt:" + text);
            return text == "hard" ? firstTurn.Task : Task.CompletedTask;
        };
        var classifier = new FakeEffortClassifier
        {
            Handler = prompt => Task.FromResult(prompt == "hard" ? EffortLevel.High : EffortLevel.Low),
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var running = SendTextAsync(vm, "hard");
        await SendTextAsync(vm, "easy");
        Assert.Equal(new[] { "effort=high", "prompt:hard" }, log);

        firstTurn.SetResult();
        await running;
        await WaitUntilAsync(() => log.Count == 4);

        Assert.Equal(new[] { "effort=high", "prompt:hard", "effort=low", "prompt:easy" }, log);
        Assert.False(vm.IsBusy);
    }

    // Stop during the judgment has no prompt to cancel yet; the turn must not start afterwards,
    // and the message the user wrote stays theirs.
    [Fact]
    public async Task AutoTurn_StoppedWhileJudging_SendsNothingAndKeepsTheDraft()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await vm.CancelAsync();
        verdict.SetResult(EffortLevel.High);
        await sending;

        Assert.Empty(log);
        Assert.Empty(connection.ConfigChanges);
        Assert.Empty(connection.Prompts);
        Assert.Equal("hard work", vm.InputText);
        Assert.False(vm.IsBusy);
    }

    // A verdict belongs to the session it was made in: a new session starts over from High and
    // shows plain "Auto" until its own first judgment.
    [Fact]
    public async Task NewSession_ForgetsThePreviousSessionsAutoVerdict()
    {
        var (connection, log) = AutoConnection("medium");
        var verdicts = new Queue<Task<EffortLevel>>(new[]
        {
            Task.FromResult(EffortLevel.Low),
            Task.FromException<EffortLevel>(new TimeoutException("slow")),
        });
        var classifier = new FakeEffortClassifier { Handler = _ => verdicts.Dequeue() };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        await SendTextAsync(vm, "git push");
        Assert.Equal("Auto · low", vm.ActiveEffortName);

        await vm.NewSessionAsync();
        Assert.Equal("Auto", vm.ActiveEffortName);
        log.Clear();
        await SendTextAsync(vm, "next");

        Assert.Equal(new[] { "effort=high", "prompt:next" }, log);
    }

    // Leaving Auto is a selection like any other: only an acknowledged change is shown.
    [Fact]
    public async Task ExplicitEffort_RejectedWhileOnAuto_StaysOnAuto()
    {
        var (connection, _) = AutoConnection("medium");
        using var vm = CreateWithClassifier(connection, new FakeEffortClassifier());
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        connection.ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"));

        await vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "xhigh"));

        Assert.Same(Auto(vm), vm.SelectedEffort);
        Assert.Equal("Auto", vm.ActiveEffortName);
    }

    // A manual change landing between Auto's classification and its own effort change would be
    // silently overwritten; settings stay locked from classification until the turn's effort is set.
    [Fact]
    public async Task AutoTurn_WhileClassifying_ManualSettingChangesAreHeldOff()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "easy");
        Assert.False(vm.CanConfigure);
        await vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "xhigh"));
        Assert.Empty(connection.ConfigChanges);

        verdict.SetResult(EffortLevel.Low);
        await sending;
        Assert.Equal(new[] { "effort=low", "prompt:easy" }, log);
        Assert.True(vm.CanConfigure);
    }

    // Explicit effort keeps the existing send-ahead behaviour.
    [Fact]
    public async Task ExplicitEffort_FollowUpIsStillSentAhead()
    {
        var (connection, log) = AutoConnection("medium");
        connection.SupportsPromptQueueing = true;
        var firstTurn = new TaskCompletionSource();
        connection.PromptHandler = content =>
        {
            var text = ((ContentBlock.Text)content[0]).Value;
            log.Add("prompt:" + text);
            return text == "first" ? firstTurn.Task : Task.CompletedTask;
        };
        using var vm = CreateWithClassifier(connection, new FakeEffortClassifier());
        await vm.Initialization;

        var running = SendTextAsync(vm, "first");
        await SendTextAsync(vm, "second");

        Assert.Equal(new[] { "prompt:first", "prompt:second" }, log);
        firstTurn.SetResult();
        await running;
    }

    private static async Task WithinAsync(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, finished);
        await task;
    }

    // The real judge runs up to 15 s; Stop must not wait for it, must not turn into a failure
    // notice, and must not let the abandoned verdict retune the agent.
    [Fact]
    public async Task AutoTurn_StopWhileJudging_CancelsTheJudgeAtOnce_WithoutFailureNoticeOrEffortChange()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return EffortLevel.High;
            },
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await WithinAsync(vm.CancelAsync());
        await WithinAsync(sending);

        Assert.True(classifier.Tokens[0].IsCancellationRequested);
        Assert.Empty(log);
        Assert.Empty(connection.ConfigChanges);
        Assert.DoesNotContain("could not judge", vm.StatusMessage ?? string.Empty);
        Assert.Equal("hard work", vm.InputText);
        Assert.Equal("Auto", vm.ActiveEffortName);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanConfigure);
    }

    // Stop during the agent's effort change: the request is abandoned and the picker does not move.
    [Fact]
    public async Task AutoTurn_StopWhileSettingEffort_AbandonsTheChange()
    {
        var (connection, log) = AutoConnection("medium");
        connection.ConfigHandler = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return connection.ConfigOptions;
        };
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => connection.ConfigChanges.Count == 1);
        await WithinAsync(vm.CancelAsync());
        await WithinAsync(sending);

        Assert.Empty(log);
        Assert.Equal("hard work", vm.InputText);
        Assert.DoesNotContain("Error", vm.StatusMessage ?? string.Empty);
        Assert.Equal("Auto", vm.ActiveEffortName);
        Assert.False(vm.IsBusy);
    }

    // Stop on a queued Auto turn's judgment behaves like Stop on a running turn: the follow-ups
    // stay queued, in order, and are sent once it has ended.
    [Fact]
    public async Task AutoTurn_StopWhileJudgingAQueuedMessage_KeepsTheQueueMovingInOrder()
    {
        var (connection, log) = AutoConnection("medium");
        connection.SupportsPromptQueueing = true;
        var firstTurn = new TaskCompletionSource();
        connection.PromptHandler = content =>
        {
            var text = ((ContentBlock.Text)content[0]).Value;
            log.Add("prompt:" + text);
            return text == "first" ? firstTurn.Task : Task.CompletedTask;
        };
        var judgedSecond = 0;
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (prompt, token) =>
            {
                if (prompt == "second" && judgedSecond++ == 0) await Task.Delay(Timeout.Infinite, token);
                return EffortLevel.Medium;
            },
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var running = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => log.Contains("prompt:first"));
        await SendTextAsync(vm, "second");
        await SendTextAsync(vm, "third");
        firstTurn.SetResult();
        await WithinAsync(running);
        await WaitUntilAsync(() => judgedSecond == 1);

        await WithinAsync(vm.CancelAsync());
        await WaitUntilAsync(() => log.Count == 3);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal(new[] { "prompt:first", "prompt:second", "prompt:third" }, log);
    }

    [Fact]
    public async Task AutoTurn_DisposedWhileJudging_EndsQuietly_WithoutTouchingTheAgent()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return EffortLevel.High;
            },
        };
        var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        vm.Dispose();
        await WithinAsync(sending);

        Assert.Empty(log);
        Assert.Empty(connection.ConfigChanges);
    }

    // The panel says what the wait is for, and goes back to the plain wording afterwards.
    [Fact]
    public async Task AutoTurn_WhileJudging_ActivityNamesTheJudgment()
    {
        var (connection, _) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "easy");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        Assert.Contains("effort", vm.ActivityText, StringComparison.OrdinalIgnoreCase);

        verdict.SetResult(EffortLevel.Medium);
        await sending;
        Assert.Equal(string.Empty, vm.ActivityText);
    }

    // Nothing to judge in an attachment-only message: no judge call, the last level (else High) holds.
    [Fact]
    public async Task AutoTurn_AttachmentOnly_SkipsTheJudge_AndUsesTheLastLevelElseHigh()
    {
        var (connection, log) = AutoConnection("medium");
        connection.PromptHandler = content =>
        {
            log.Add("prompt:" + content.Count);
            return Task.CompletedTask;
        };
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        vm.Attachments.Add(new ChatAttachmentViewModel("a.png", "image/png", "AAAA"));
        await SendTextAsync(vm, string.Empty);
        Assert.Empty(classifier.Prompts);
        Assert.Equal(new[] { "effort=high", "prompt:1" }, log);

        await SendTextAsync(vm, "small");
        log.Clear();
        vm.Attachments.Add(new ChatAttachmentViewModel("b.png", "image/png", "BBBB"));
        await SendTextAsync(vm, string.Empty);

        Assert.Equal(new[] { "small" }, classifier.Prompts);
        Assert.Equal(new[] { "prompt:1" }, log);
        Assert.Equal("Auto · low", vm.ActiveEffortName);
    }

    // A queued Auto turn whose effort change is refused has no live draft to keep: like any turn
    // that fails before it starts, the message goes back to the message box and is not sent.
    [Fact]
    public async Task AutoTurn_EffortChangeRejectedOnAQueuedTurn_ReturnsItToTheComposer()
    {
        var (connection, log) = AutoConnection("medium");
        connection.SupportsPromptQueueing = true;
        var firstTurn = new TaskCompletionSource();
        connection.PromptHandler = content =>
        {
            var text = ((ContentBlock.Text)content[0]).Value;
            log.Add("prompt:" + text);
            return text == "first" ? firstTurn.Task : Task.CompletedTask;
        };
        connection.ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"));
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Medium) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var running = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => log.Contains("prompt:first"));
        await SendTextAsync(vm, "second");
        classifier.Handler = _ => Task.FromResult(EffortLevel.High);
        firstTurn.SetResult();
        await WithinAsync(running);
        await WaitUntilAsync(() => !vm.IsBusy && vm.InputText == "second");

        Assert.Equal(new[] { "prompt:first" }, log);
        Assert.Contains("rejected", vm.StatusMessage);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User && message.Text == "second");
    }

    // Auto picked while a turn is running still governs the next turn, which then waits for it.
    [Fact]
    public async Task AutoSelectedDuringARunningTurn_FollowUpWaitsAndGetsItsOwnEffort()
    {
        var (connection, log) = AutoConnection("medium");
        connection.SupportsPromptQueueing = true;
        var firstTurn = new TaskCompletionSource();
        connection.PromptHandler = content =>
        {
            var text = ((ContentBlock.Text)content[0]).Value;
            log.Add("prompt:" + text);
            return text == "first" ? firstTurn.Task : Task.CompletedTask;
        };
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;

        var running = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => log.Contains("prompt:first"));
        await vm.SelectEffortAsync(Auto(vm));
        await SendTextAsync(vm, "second");
        Assert.Equal(new[] { "prompt:first" }, log);

        firstTurn.SetResult();
        await WithinAsync(running);
        await WaitUntilAsync(() => log.Count == 3);

        Assert.Equal(new[] { "prompt:first", "effort=low", "prompt:second" }, log);
    }

    // Auto is only offered while the agent advertises low, medium and high; an update that no
    // longer does falls back to the agent's own level, and turns then run as explicit ones.
    [Fact]
    public async Task ConfigUpdateWithoutTheThreeLevels_DropsAuto_AndNoJudgmentFollows()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier();
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        connection.RaiseSessionUpdate(new SessionUpdate.ConfigOptionsChanged(Options("sonnet", "xhigh", "xhigh", "max")));

        Assert.DoesNotContain(vm.AvailableEfforts, value => value.Name == "Auto");
        Assert.Equal("xhigh", vm.SelectedEffort!.Value);
        await SendTextAsync(vm, "go");
        Assert.Empty(classifier.Prompts);
        Assert.Equal(new[] { "prompt:go" }, log);
    }
}
