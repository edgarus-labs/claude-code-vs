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

    // Auto's ceiling is High: xhigh/max stay available, but only as an explicit choice. Auto started
    // from one of them sets the level it judged, not the one it started from.
    [Fact]
    public async Task AutoTurn_StartingAboveHigh_SetsTheJudgedLevel()
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
        // The fallback level is still the level the turn runs at, so it is shown too.
        Assert.Equal("Auto · high", vm.ActiveEffortName);
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
    public async Task AutoTurn_EffortChangeRejected_ReturnsTheDraftAndSendsNothing()
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
        // The turn's failure says Auto effort caused it, not just what the agent answered.
        Assert.Contains("Auto effort could not set the effort to high", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    // F-R2: a message written while an earlier draft is judged is queued behind it. If the earlier
    // draft then fails before it is sent, the later one must not overtake it: both go back to the
    // composer, in the order they were written, and nothing reaches the agent.
    [Fact]
    public async Task AutoTurn_DraftFailsBeforeItIsSent_QueuedFollowUpDoesNotOvertakeIt()
    {
        var (connection, log) = AutoConnection("medium");
        connection.ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"));
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "first message");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "second message");
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Empty(log);
        Assert.Empty(connection.Prompts);
        Assert.Equal("first message" + Environment.NewLine + Environment.NewLine + "second message", vm.InputText);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User);
    }

    // Text typed after the queued follow-up was written after it too: when the draft fails before it
    // is sent, the message box holds all three in the order they were written. Nothing Claude never
    // received names the chat.
    [Fact]
    public async Task AutoTurn_DraftFailsBeforeItIsSent_QueuedFollowUpAndTextTypedSince_ComeBackInWrittenOrder()
    {
        var (connection, log) = AutoConnection("medium");
        connection.ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"));
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "second");
        vm.InputText = "third";
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Empty(log);
        Assert.Empty(connection.Prompts);
        Assert.Equal(string.Join(Environment.NewLine + Environment.NewLine, "first", "second", "third"), vm.InputText);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User);
        Assert.Equal("Untitled", vm.SessionTitle);
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
        await WaitUntilAsync(() => log.Count == 4 && !vm.IsBusy);

        Assert.Equal(new[] { "effort=high", "prompt:hard", "effort=low", "prompt:easy" }, log);
        Assert.False(vm.IsBusy);
    }

    // #53: a sent message belongs to the turn, not the composer. While Auto judges it, it shows in
    // the transcript as pending (like a queued message) and the composer is already clear for the
    // next one; once the prompt goes out, the bubble stops reading as pending.
    [Fact]
    public async Task AutoTurn_WhileJudging_MessageLeavesTheComposer_AndShowsPendingInTheTranscript()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        var attachment = new ChatAttachmentViewModel("a.png", "image/png", "AAAA");
        vm.Attachments.Add(attachment);

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);

        Assert.Equal(string.Empty, vm.InputText);
        Assert.Empty(vm.Attachments);
        var bubble = Assert.Single(vm.Messages, message => message.Role == ChatRole.User);
        Assert.Equal("hard work", bubble.Text);
        Assert.True(bubble.IsPending);
        Assert.Single(bubble.Images);
        Assert.True(vm.IsBusy);

        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal(new[] { "effort=high", "prompt:hard work" }, log);
        Assert.Same(bubble, Assert.Single(vm.Messages, message => message.Role == ChatRole.User));
        Assert.False(bubble.IsPending);
    }

    // #53: Stop while judging ends the turn before the prompt went out, so the message is not left
    // in the transcript as if Claude had seen it: it comes back to the composer, ahead of whatever
    // was typed there since, with its attachments.
    [Fact]
    public async Task AutoTurn_StoppedWhileJudging_ReturnsTheMessageToTheComposer_AheadOfWhatWasTypedSince()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        var attachment = new ChatAttachmentViewModel("a.png", "image/png", "AAAA");
        vm.Attachments.Add(attachment);

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        vm.InputText = "typed since";
        await vm.CancelAsync();
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Empty(log);
        Assert.Equal("hard work" + Environment.NewLine + Environment.NewLine + "typed since", vm.InputText);
        Assert.Same(attachment, Assert.Single(vm.Attachments));
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User);
        Assert.Equal("Untitled", vm.SessionTitle);
    }

    // Stop during the judgment has no prompt to cancel yet; the turn must not start afterwards,
    // and the message the user wrote comes back to the composer.
    [Fact]
    public async Task AutoTurn_StoppedWhileJudging_SendsNothingAndReturnsTheDraft()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
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
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        Assert.False(vm.CanConfigure);
        await vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "xhigh"));
        Assert.Empty(connection.ConfigChanges);
        Assert.Same(Auto(vm), vm.SelectedEffort);

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
        Assert.True(string.IsNullOrEmpty(vm.StatusMessage), vm.StatusMessage);
        Assert.Equal("hard work", vm.InputText);
        Assert.Equal("Auto", vm.ActiveEffortName);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanConfigure);
    }

    // The agent's effort request number `call` (0-based) is answered only once `Release` completes;
    // the others go through. Like the real agent, the change is applied when the request arrives and
    // acknowledged when answered. `Tokens` holds what each request was sent with: cancelling a request
    // makes the real connection drop the whole session, so Stop must never be what cancels one.
    private static (TaskCompletionSource Release, List<CancellationToken> Tokens) StallEffortRequest(
        RecordingAcpAgentConnection connection, int call = 0)
    {
        var inner = connection.ConfigHandler!;
        var release = new TaskCompletionSource();
        var tokens = new List<CancellationToken>();
        var calls = 0;
        connection.ConfigHandler = async (session, value, token) =>
        {
            tokens.Add(token);
            var acknowledgement = inner(session, value, token);
            if (calls++ == call) await release.Task;
            return await acknowledgement;
        };
        return (release, tokens);
    }

    // Presses Stop once the agent holds `requests` effort requests.
    private static async Task StopOnceRequestedAsync(ChatViewModel vm, RecordingAcpAgentConnection connection, int requests = 1)
    {
        await WaitUntilAsync(() => connection.ConfigChanges.Count == requests);
        await WithinAsync(vm.CancelAsync());
    }

    // Stop during the agent's effort change does not cancel the request: the answer still lands and
    // is the level shown, the turn ends without sending, and the message comes back to the composer.
    [Fact]
    public async Task AutoTurn_StopWhileSettingEffort_LetsTheRequestFinish_ThenEndsTheTurnWithoutSending()
    {
        var (connection, log) = AutoConnection("medium");
        var (release, tokens) = StallEffortRequest(connection);
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await StopOnceRequestedAsync(vm, connection);
        Assert.All(tokens, token => Assert.False(token.IsCancellationRequested));
        Assert.Equal(0, connection.CancelCount);
        release.SetResult();
        await WithinAsync(sending);

        Assert.Equal(new[] { "effort=high" }, log);
        Assert.Empty(connection.Prompts);
        Assert.Equal("hard work", vm.InputText);
        Assert.True(string.IsNullOrEmpty(vm.StatusMessage), vm.StatusMessage);
        Assert.Equal("Auto · high", vm.ActiveEffortName);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanConfigure);
    }

    // The request answers just as Stop is pressed: the answer is applied, but the turn Stop ended
    // must not send its prompt anyway.
    [Fact]
    public async Task AutoTurn_EffortRequestAnswersAsStopIsPressed_EndsTheTurnWithoutSending()
    {
        var (connection, log) = AutoConnection("medium");
        var inner = connection.ConfigHandler!;
        Func<Task> stop = () => Task.CompletedTask;
        connection.ConfigHandler = async (session, value, token) =>
        {
            var acknowledgement = await inner(session, value, token);
            await stop();
            return acknowledgement;
        };
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        stop = vm.CancelAsync;
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await WithinAsync(SendTextAsync(vm, "hard work"));

        Assert.Equal(new[] { "effort=high" }, log);
        Assert.Empty(connection.Prompts);
        Assert.Equal("hard work", vm.InputText);
        Assert.True(string.IsNullOrEmpty(vm.StatusMessage), vm.StatusMessage);
        Assert.Equal("Auto · high", vm.ActiveEffortName);
        Assert.False(vm.IsBusy);
    }

    // Stop on a queued Auto turn's judgment behaves like Stop on a running turn: the stopped message
    // is queued again ahead of the follow-ups, and all go out in order once it has ended. The
    // stopped message is judged afresh, and nothing reports the Stop as a failure.
    [Fact]
    public async Task AutoTurn_StopWhileJudgingAQueuedMessage_RequeuesItAheadOfTheFollowUps()
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
        Assert.Equal(2, judgedSecond);
        Assert.True(string.IsNullOrEmpty(vm.StatusMessage), vm.StatusMessage);
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
        Assert.Equal("Judging effort…", vm.ActivityText);

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

        // Whitespace beside an attachment is no more to judge than nothing.
        vm.Attachments.Add(new ChatAttachmentViewModel("c.png", "image/png", "CCCC"));
        await SendTextAsync(vm, "   ");
        Assert.Equal(2, log.Count);
        Assert.Equal(new[] { "small" }, classifier.Prompts);
    }

    // A queued Auto turn whose effort change is refused fails before it starts: like a live message,
    // it goes back to the message box and is not sent.
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

    // The judgment locks the settings, not the composer. A message written meanwhile is
    // queued like one written during any running turn, and goes out after this turn, in order.
    [Fact]
    public async Task AutoTurn_WhileJudging_ComposerStaysUsable_AndTheNextMessageIsQueued()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        Assert.False(vm.IsConfigBusy);
        Assert.False(vm.CanConfigure);

        await SendTextAsync(vm, "second");
        Assert.Equal(string.Empty, vm.InputText);
        Assert.Contains(vm.Messages, message => message.Role == ChatRole.User && message.Text == "second");

        classifier.Handler = _ => Task.FromResult(EffortLevel.High);
        verdict.SetResult(EffortLevel.Low);
        await sending;
        await WaitUntilAsync(() => log.Contains("prompt:second"));

        // Each message is judged on its own: the queued one gets its own, different, level.
        Assert.Equal(new[] { "first", "second" }, classifier.Prompts);
        Assert.Equal(new[] { "effort=low", "prompt:first", "effort=high", "prompt:second" }, log);
    }

    // The judged message has already left the composer (#53), so a second Enter finds it empty and
    // sends nothing: the prompt runs once, and the transcript shows the message once.
    [Fact]
    public async Task AutoTurn_WhileJudging_ASecondEnter_FindsTheComposerEmpty_AndQueuesNoCopy()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        Assert.Equal(string.Empty, vm.InputText);
        await vm.SendAsync();
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal(new[] { "hard work" }, classifier.Prompts);
        Assert.Equal(new[] { "effort=high", "prompt:hard work" }, log);
        Assert.Single(vm.Messages, message => message.Role == ChatRole.User);
        Assert.Equal(string.Empty, vm.InputText);
    }

    // A new message written while the first is judged is queued at once and sent exactly once after
    // the first.
    [Fact]
    public async Task AutoTurn_WhileJudging_ANewMessage_IsSentOnceAfterTheFirst()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "hard work, then add tests");
        Assert.Equal(string.Empty, vm.InputText);

        classifier.Handler = _ => Task.FromResult(EffortLevel.High);
        verdict.SetResult(EffortLevel.Low);
        await WithinAsync(sending);
        await WaitUntilAsync(() => log.Contains("prompt:hard work, then add tests") && !vm.IsBusy);

        Assert.Equal(new[] { "effort=low", "prompt:hard work", "effort=high", "prompt:hard work, then add tests" }, log);
        Assert.Single(vm.Messages, message => message.Role == ChatRole.User && message.Text == "hard work");
        Assert.Single(vm.Messages, message => message.Role == ChatRole.User && message.Text == "hard work, then add tests");
    }

    // The same text typed again with an attachment is a new message like any other.
    [Fact]
    public async Task AutoTurn_WhileJudging_SameTextWithANewAttachment_IsANewMessage()
    {
        var (connection, _) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "look at this");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        vm.Attachments.Add(new ChatAttachmentViewModel("a.png", "image/png", "AAAA"));
        await SendTextAsync(vm, "look at this");

        classifier.Handler = _ => Task.FromResult(EffortLevel.Medium);
        verdict.SetResult(EffortLevel.Medium);
        await WithinAsync(sending);
        await WaitUntilAsync(() => connection.Prompts.Count == 2 && !vm.IsBusy);

        Assert.Equal(new[] { 1, 2 }, connection.Prompts.Select(prompt => prompt.Count));
    }

    // A message stopped while judged comes back to the composer with its attachments, also when
    // another message was sent meanwhile.
    [Fact]
    public async Task AutoTurn_StoppedWhileJudging_AfterAnotherMessageWasSent_ComesBackWithItsAttachments()
    {
        var (connection, _) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (prompt, token) =>
            {
                if (prompt == "look at this") await Task.Delay(Timeout.Infinite, token);
                return EffortLevel.Medium;
            },
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        var attachment = new ChatAttachmentViewModel("a.png", "image/png", "AAAA");
        vm.Attachments.Add(attachment);

        var sending = SendTextAsync(vm, "look at this");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "and then this");
        Assert.Empty(vm.Attachments);
        await WithinAsync(vm.CancelAsync());
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal("look at this", vm.InputText);
        Assert.Contains(attachment, vm.Attachments);
    }

    // A message whose judgment Stop ended is back in the composer: sent again while another message
    // is judged, it is a message like any other.
    [Fact]
    public async Task AutoTurn_DraftStoppedWhileJudging_IsANewMessage_WhenTheSameTextIsSentAgain()
    {
        var (connection, log) = AutoConnection("medium");
        var secondVerdict = new TaskCompletionSource<EffortLevel>();
        var judgedHard = 0;
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (prompt, token) =>
            {
                if (prompt == "second") return await secondVerdict.Task;
                if (judgedHard++ == 0) await Task.Delay(Timeout.Infinite, token);
                return EffortLevel.Medium;
            },
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "second");
        await WithinAsync(vm.CancelAsync());
        await WithinAsync(sending);
        await WaitUntilAsync(() => classifier.Prompts.Contains("second"));

        await SendTextAsync(vm, "hard work");
        Assert.Equal(string.Empty, vm.InputText);
        secondVerdict.SetResult(EffortLevel.Medium);
        await WaitUntilAsync(() => log.Contains("prompt:hard work") && !vm.IsBusy);

        Assert.Equal(new[] { "prompt:second", "prompt:hard work" }, log);
    }

    // The same text sent again while the first runs is a second message.
    [Fact]
    public async Task AutoTurn_SameTextSentAgainWhileTheFirstRuns_IsQueued()
    {
        var (connection, log) = AutoConnection("medium");
        var firstTurn = new TaskCompletionSource();
        connection.PromptHandler = content =>
        {
            log.Add("prompt:" + ((ContentBlock.Text)content[0]).Value);
            return log.Count == 1 ? firstTurn.Task : Task.CompletedTask;
        };
        using var vm = CreateWithClassifier(connection, new FakeEffortClassifier());
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var running = SendTextAsync(vm, "again");
        await WaitUntilAsync(() => log.Contains("prompt:again"));
        await SendTextAsync(vm, "again");
        Assert.Equal(string.Empty, vm.InputText);
        firstTurn.SetResult();
        await WithinAsync(running);
        await WaitUntilAsync(() => log.Count == 2 && !vm.IsBusy);

        Assert.Equal(new[] { "prompt:again", "prompt:again" }, log);
    }

    // The level the agent acknowledged after a stopped Auto turn is the one it runs at: the next Auto
    // turn sets its own level only when it differs.
    [Fact]
    public async Task AutoTurn_AfterAStoppedEffortChange_NextTurnsCompareWithTheAcknowledgedLevel()
    {
        var (connection, log) = AutoConnection("medium");
        var (release, _) = StallEffortRequest(connection);
        var classifier = new FakeEffortClassifier { Handler = prompt => Task.FromResult(prompt == "easy" ? EffortLevel.Medium : EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        var sending = SendTextAsync(vm, "hard");
        await StopOnceRequestedAsync(vm, connection);
        release.SetResult();
        await WithinAsync(sending);
        Assert.Empty(connection.Prompts);
        log.Clear();

        await SendTextAsync(vm, "hard again");
        Assert.Equal(new[] { "prompt:hard again" }, log);

        await SendTextAsync(vm, "easy");
        Assert.Equal(new[] { "prompt:hard again", "effort=medium", "prompt:easy" }, log);
    }

    // The judgment is a multi-second await between connecting and sending. A session lost in
    // that window (agent died, sign-out, workspace switch) must not send the message to the
    // connection that was released: it goes back to the composer and leaves the transcript.
    [Fact]
    public async Task AutoTurn_SessionLostWhileJudging_SendsNothingAndReturnsTheDraft()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        connection.RaiseDisconnected();
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);

        Assert.Empty(connection.Prompts);
        Assert.Empty(connection.ConfigChanges);
        Assert.Empty(log);
        Assert.Equal("hard work", vm.InputText);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User);
        Assert.False(vm.IsBusy);
    }

    // A session lost while judging ends that judgment at once: the judge (a CLI process) is not left
    // running for a session that is gone, and the turn does not hold the panel busy until it returns.
    [Fact]
    public async Task AutoTurn_SessionLostWhileJudging_CancelsTheJudgmentAndEndsTheTurn()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return EffortLevel.Medium;
            },
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        connection.RaiseDisconnected();
        await WithinAsync(sending);

        Assert.False(vm.IsBusy);
        Assert.Empty(log);
        Assert.Equal("hard work", vm.InputText);
    }

    // Written while the first is still being judged, the second message is queued at once, below
    // the first one's bubble.
    [Fact]
    public async Task AutoTurn_WhileJudging_TheJudgedDraftKeepsItsPlaceAboveALaterQueuedMessage()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "second");
        classifier.Handler = _ => Task.FromResult(EffortLevel.Medium);
        verdict.SetResult(EffortLevel.Medium);
        await WithinAsync(sending);
        await WaitUntilAsync(() => log.Contains("prompt:second") && !vm.IsBusy);

        Assert.Equal(new[] { "first", "second" },
            vm.Messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray());
    }

    // When Stop ends the first message's judgment after a later one was queued, the first comes back
    // to the message box and leaves the transcript, and the queued one is still sent - and names the chat.
    [Fact]
    public async Task AutoTurn_JudgedDraftStoppedAfterALaterMessageWasQueued_ReturnsToTheComposer()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (prompt, token) =>
            {
                if (prompt == "first") await Task.Delay(Timeout.Infinite, token);
                return EffortLevel.Medium;
            },
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "second");
        await WithinAsync(vm.CancelAsync());
        await WithinAsync(sending);
        await WaitUntilAsync(() => log.Contains("prompt:second") && !vm.IsBusy);

        Assert.Equal(new[] { "prompt:second" }, log);
        Assert.Equal("first", vm.InputText);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User && message.Text == "first");
        Assert.Equal("second", vm.SessionTitle);
    }

    // A queued message being judged is held by nobody else, so a session lost meanwhile
    // takes it too: it is reported like every other queued message the session drops, and its
    // bubble does not stay behind as pending.
    [Fact]
    public async Task AutoTurn_SessionLostWhileJudgingAQueuedMessage_DropsItWithNotice()
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
        var secondVerdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier
        {
            Handler = prompt => prompt == "second" ? secondVerdict.Task : Task.FromResult(EffortLevel.Medium),
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var running = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => log.Contains("prompt:first"));
        await SendTextAsync(vm, "second");
        firstTurn.SetResult();
        await WithinAsync(running);
        await WaitUntilAsync(() => classifier.Prompts.Contains("second"));

        connection.RaiseDisconnected();
        secondVerdict.SetResult(EffortLevel.High);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal(new[] { "prompt:first" }, log);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User && message.Text == "second");
        Assert.Contains("A queued message was not sent.", vm.StatusMessage);
    }

    // Auto is the user's choice, not a property of one connection. A disconnect, sign-out or
    // workspace switch tears the session down and empties the pickers, but the reconnected session
    // is still on Auto.
    [Fact]
    public async Task Auto_SurvivesAReconnect()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        connection.RaiseDisconnected();
        await SendTextAsync(vm, "git status");

        Assert.Equal(new[] { "git status" }, classifier.Prompts);
        Assert.Equal(new[] { "effort=low", "prompt:git status" }, log);
        Assert.Same(Auto(vm), vm.SelectedEffort);
    }

    // Picking a level ends the Auto choice for good, also while Auto is not on offer.
    [Fact]
    public async Task ExplicitEffort_EndsTheAutoChoice_AlsoAcrossAReconnect()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier();
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        await vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "xhigh"));

        connection.RaiseDisconnected();
        log.Clear();
        await SendTextAsync(vm, "hello");

        Assert.Empty(classifier.Prompts);
        Assert.Equal(new[] { "prompt:hello" }, log);
        Assert.Equal("medium", vm.SelectedEffort!.Value);
        Assert.NotEqual("Auto", vm.ActiveEffortName);
    }

    // The judgment has no prompt in flight, so Stop has nothing to cancel at the agent: it ends the
    // judgment and the turn, and an agent that would refuse a cancel is never asked.
    [Fact]
    public async Task AutoTurn_StopWhileJudging_NeverAsksTheAgentToCancel_AndEndsTheTurn()
    {
        var (connection, log) = AutoConnection("medium");
        connection.CancelHandler = () => Task.FromException(new InvalidOperationException("cancel refused"));
        var judgeSawCancel = new TaskCompletionSource();
        var releaseJudge = new TaskCompletionSource();
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (_, token) =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException)
                {
                    judgeSawCancel.SetResult();
                    await releaseJudge.Task;
                    throw;
                }
                return EffortLevel.High;
            },
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await WithinAsync(vm.CancelAsync());
        await WithinAsync(judgeSawCancel.Task);
        releaseJudge.SetResult();
        await WithinAsync(sending);

        Assert.Empty(log);
        Assert.Empty(connection.ConfigChanges);
        Assert.Equal("hard work", vm.InputText);
        Assert.Equal(0, connection.CancelCount);
        Assert.True(string.IsNullOrEmpty(vm.StatusMessage), vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    // Only Stop or dispose cancel a judgment on purpose. A judge that ends in a cancellation
    // or throws on its own is a failed judgment: the turn is not silently dropped, it falls back and
    // is sent, with the reason shown.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutoTurn_JudgeEndsInACancellationOrThrowsOnItsOwn_FallsBackAndStillSendsTheTurn(bool cancellation)
    {
        var (connection, log) = AutoConnection("low");
        var classifier = new FakeEffortClassifier
        {
            Handler = _ => cancellation
                ? Task.FromCanceled<EffortLevel>(new CancellationToken(canceled: true))
                : throw new InvalidOperationException("judge blew up"),
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "hello");

        Assert.Equal(new[] { "effort=high", "prompt:hello" }, log);
        Assert.Contains("could not judge", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    // A judge that times out on its own token ends in a cancellation nobody pressed Stop for: after a
    // verdict the turn keeps that level (not the first-judgment High), is still sent, and the failure
    // is said and logged.
    [Fact]
    public async Task AutoTurn_JudgeCancelsItselfAfterAVerdict_KeepsTheLastLevel_SaysAndLogsIt()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (prompt, _) =>
            {
                if (prompt == "git push") return EffortLevel.Low;
                using var timeout = new CancellationTokenSource();
                timeout.Cancel();
                await Task.Delay(Timeout.Infinite, timeout.Token);
                return EffortLevel.High;
            },
        };
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            EffortClassifier = classifier,
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        await SendTextAsync(vm, "git push");

        await SendTextAsync(vm, "second");

        Assert.Equal(new[] { "effort=low", "prompt:git push", "prompt:second" }, log);
        Assert.False(classifier.Tokens[1].IsCancellationRequested);
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
        Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(services.LoggedErrors).Exception);
        Assert.Equal("Auto · low", vm.ActiveEffortName);
        Assert.False(vm.IsBusy);
    }

    // Resuming a session starts over like a new one: the previous session's verdict says nothing
    // about it.
    [Fact]
    public async Task ResumedSession_ForgetsThePreviousSessionsAutoVerdict()
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

        await vm.OpenSessionAsync(new SessionSummary("session-2", "/workspace", "Older chat", null));
        Assert.Equal("Auto", vm.ActiveEffortName);
        log.Clear();
        await SendTextAsync(vm, "next");

        Assert.Equal(new[] { "effort=high", "prompt:next" }, log);
    }

    // Auto chooses among all three levels, so an agent that lacks any one of them cannot offer it.
    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public async Task AutoEffort_NotOffered_WhenTheAgentLacksAnyOneOfLowMediumHigh(string missing)
    {
        var levels = new[] { "low", "medium", "high", "xhigh" }.Where(level => level != missing).ToArray();
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options("sonnet", levels[0], levels) };
        using var vm = CreateWithClassifier(connection, new FakeEffortClassifier());
        await vm.Initialization;

        Assert.DoesNotContain(vm.AvailableEfforts, value => value.Name == "Auto");
        Assert.Equal(levels, vm.AvailableEfforts.Select(value => value.Value));
    }

    // Sonnet advertises every level and starts on medium; opus advertises `opusLevels` and the agent
    // moves the effort to `opusDefault` when it is picked, like the real adapter does per model.
    private static (RecordingAcpAgentConnection Connection, List<string> Log) ModelSwitchingConnection(string[] opusLevels, string opusDefault)
    {
        var (connection, log) = AutoConnection("medium");
        var model = "sonnet";
        var effort = "medium";
        connection.ConfigHandler = (id, value, _) =>
        {
            if (id == "model-choice")
            {
                model = value;
                effort = model == "opus" ? opusDefault : "medium";
                log.Add("model=" + value);
            }
            else
            {
                effort = value;
                log.Add("effort=" + value);
            }
            return Task.FromResult(Options(model, effort, model == "opus" ? opusLevels : AdvertisedEfforts));
        };
        return (connection, log);
    }

    // Auto stays the user's choice across a model switch, but is only on offer while the model has all
    // three levels: on one without, turns run as explicit ones (the remembered level is never applied
    // to it), and Auto is back, judging afresh, once a model with all three is picked again.
    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public async Task ModelSwitch_ToAModelLackingALevel_SuspendsAuto_UntilAModelWithAllThreeReturns(string missing)
    {
        var opusLevels = AdvertisedEfforts.Where(level => level != missing).ToArray();
        var (connection, log) = ModelSwitchingConnection(opusLevels, "xhigh");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        await SendTextAsync(vm, "git push");

        await vm.SelectModelAsync(vm.AvailableModels.Single(value => value.Value == "opus"));
        Assert.DoesNotContain(vm.AvailableEfforts, value => value.Name == "Auto");
        Assert.Equal(opusLevels, vm.AvailableEfforts.Select(value => value.Value));
        Assert.Equal("xhigh", vm.SelectedEffort!.Value);
        log.Clear();
        await SendTextAsync(vm, "next");

        Assert.Equal(new[] { "prompt:next" }, log);
        Assert.Equal(new[] { "git push" }, classifier.Prompts);

        classifier.Handler = _ => Task.FromResult(EffortLevel.High);
        await vm.SelectModelAsync(vm.AvailableModels.Single(value => value.Value == "sonnet"));
        Assert.Same(Auto(vm), vm.SelectedEffort);
        log.Clear();
        await SendTextAsync(vm, "again");

        Assert.Equal(new[] { "effort=high", "prompt:again" }, log);
    }

    // A model that has all three levels keeps Auto: the next turn is judged, and its level is set
    // whatever the agent moved the effort to on the switch.
    [Fact]
    public async Task ModelSwitch_ToAModelWithAllThreeLevels_KeepsAutoWorking()
    {
        var (connection, log) = ModelSwitchingConnection(AdvertisedEfforts, "high");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        await SendTextAsync(vm, "git push");

        await vm.SelectModelAsync(vm.AvailableModels.Single(value => value.Value == "opus"));
        Assert.Same(Auto(vm), vm.SelectedEffort);
        log.Clear();
        await SendTextAsync(vm, "quick");

        Assert.Equal(new[] { "effort=low", "prompt:quick" }, log);
        Assert.Equal(new[] { "git push", "quick" }, classifier.Prompts);
        Assert.Equal("Auto · low", vm.ActiveEffortName);
    }

    // F-R12: "Auto · x" names a level Auto set. After a model switch the agent's own default is not one,
    // so the picker shows plain "Auto" until the next judgment.
    [Fact]
    public async Task ModelSwitch_MovesTheAgentsEffort_PickerNamesNoLevelAutoDidNotSet()
    {
        var (connection, _) = ModelSwitchingConnection(AdvertisedEfforts, "high");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        await SendTextAsync(vm, "git push");
        Assert.Equal("Auto · low", vm.ActiveEffortName);

        await vm.SelectModelAsync(vm.AvailableModels.Single(value => value.Value == "opus"));

        Assert.Equal("Auto", vm.ActiveEffortName);
    }

    // Dispose while the agent is applying the judged effort cancels that request (the one thing that
    // may) and ends the turn quietly: no prompt goes out on the way down, nothing is reported as a
    // failure, and nothing throws into the caller.
    [Fact]
    public async Task AutoTurn_DisposedWhileSettingEffort_EndsQuietly_WithoutSendingThePrompt()
    {
        var (connection, _) = AutoConnection("medium");
        var tokens = new List<CancellationToken>();
        connection.ConfigHandler = async (_, _, token) =>
        {
            tokens.Add(token);
            await Task.Delay(Timeout.Infinite, token);
            return connection.ConfigOptions;
        };
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            EffortClassifier = classifier,
        };
        var vm = new ChatViewModel(services);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => connection.ConfigChanges.Count == 1);
        vm.Dispose();
        await WithinAsync(sending);

        Assert.True(tokens.Single().IsCancellationRequested);
        Assert.Empty(connection.Prompts);
        Assert.True(string.IsNullOrEmpty(vm.StatusMessage), vm.StatusMessage);
        Assert.Empty(services.LoggedErrors);
    }

    // A manual pick made while an Auto follow-up waits governs that follow-up: the queue is released
    // by the pick's own request finishing, and Auto must not judge and retune over it.
    [Fact]
    public async Task ExplicitEffort_PickedWhileAnAutoFollowUpWaits_IsNotOverriddenByTheJudge()
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
        var inner = connection.ConfigHandler!;
        var holdRequests = false;
        var release = new TaskCompletionSource();
        connection.ConfigHandler = async (session, value, token) =>
        {
            if (holdRequests) await release.Task;
            return await inner(session, value, token);
        };
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var running = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => log.Contains("prompt:first"));
        await SendTextAsync(vm, "second");
        holdRequests = true;
        var picking = vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "xhigh"));
        await WaitUntilAsync(() => connection.ConfigChanges.Count == 2);
        firstTurn.SetResult();
        await WithinAsync(running);
        release.SetResult();
        await WithinAsync(picking);
        await WaitUntilAsync(() => log.Contains("prompt:second") && !vm.IsBusy);

        Assert.Equal(new[] { "first" }, classifier.Prompts);
        Assert.Equal(new[] { "effort=low", "prompt:first", "effort=xhigh", "prompt:second" }, log);
        Assert.Equal("xhigh", vm.SelectedEffort!.Value);
    }

    // F-50-85: a pick the agent rejects leaves Auto selected, so the follow-up that waited for the pick
    // is still an Auto turn: judged, and run under its own level.
    [Fact]
    public async Task ExplicitEffort_RejectedWhileAnAutoFollowUpWaits_StillJudgesTheFollowUp()
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
        var inner = connection.ConfigHandler!;
        var holdRequests = false;
        var release = new TaskCompletionSource();
        connection.ConfigHandler = async (session, value, token) =>
        {
            if (holdRequests) await release.Task;
            if (value == "xhigh") throw new InvalidOperationException("rejected");
            return await inner(session, value, token);
        };
        var classifier = new FakeEffortClassifier
        {
            Handler = prompt => Task.FromResult(prompt == "first" ? EffortLevel.Low : EffortLevel.High),
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var running = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => log.Contains("prompt:first"));
        await SendTextAsync(vm, "second");
        holdRequests = true;
        var picking = vm.SelectEffortAsync(vm.AvailableEfforts.Single(value => value.Value == "xhigh"));
        await WaitUntilAsync(() => connection.ConfigChanges.Count == 2);
        firstTurn.SetResult();
        await WithinAsync(running);
        release.SetResult();
        await WithinAsync(picking);
        await WaitUntilAsync(() => log.Contains("prompt:second") && !vm.IsBusy);

        Assert.Equal(new[] { "first", "second" }, classifier.Prompts);
        Assert.Equal(new[] { "effort=low", "prompt:first", "effort=high", "prompt:second" }, log);
        Assert.Same(Auto(vm), vm.SelectedEffort);
    }

    // The verdict is only applied while the agent still offers its level: if an update took the
    // levels away meanwhile, Auto is off and the turn goes out under the agent's own level.
    [Fact]
    public async Task AutoTurn_LevelsWithdrawnWhileJudging_SendsTheTurnWithoutSettingEffort()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        connection.RaiseSessionUpdate(new SessionUpdate.ConfigOptionsChanged(Options("sonnet", "xhigh", "xhigh", "max")));
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);

        Assert.Empty(connection.ConfigChanges);
        Assert.Equal(new[] { "prompt:hard work" }, log);
        Assert.Equal("xhigh", vm.SelectedEffort!.Value);
    }

    // A failed judgment leaves a trace beyond the status line, and the status never ends in an empty
    // reason.
    [Theory]
    [InlineData("judge blew up", "judge blew up")]
    [InlineData("", "InvalidOperationException")]
    public async Task AutoTurn_JudgeFails_LogsTheException_AndNeverShowsAnEmptyReason(string message, string shown)
    {
        var (connection, _) = AutoConnection("low");
        var error = new InvalidOperationException(message);
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromException<EffortLevel>(error) };
        var services = new StubChatSessionServices(new SingleConnectionFactory(connection), new AlwaysSignedInAuthService())
        {
            EffortClassifier = classifier,
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "hello");

        var logged = Assert.Single(services.LoggedErrors);
        Assert.Same(error, logged.Exception);
        Assert.EndsWith(": " + shown, vm.StatusMessage);
    }

    // A Stop after an earlier turn's level: the picker shows the level the agent acknowledged for the
    // stopped turn, not the earlier turn's.
    [Fact]
    public async Task AutoTurn_StopWhileSettingALaterEffort_ShowsTheAcknowledgedLevel()
    {
        var (connection, _) = AutoConnection("medium");
        var (release, _) = StallEffortRequest(connection, call: 1);
        var classifier = new FakeEffortClassifier { Handler = prompt => Task.FromResult(prompt == "easy" ? EffortLevel.Low : EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        await SendTextAsync(vm, "easy");
        Assert.Equal("Auto · low", vm.ActiveEffortName);

        var sending = SendTextAsync(vm, "hard");
        await StopOnceRequestedAsync(vm, connection, requests: 2);
        release.SetResult();
        await WithinAsync(sending);
        Assert.Single(connection.Prompts); // "easy" only: the stopped turn sent nothing

        Assert.Equal("Auto · high", vm.ActiveEffortName);
    }

    // A message that silently does not go out is explained even when nothing else reported why
    // (here a workspace switch, which clears the status before it tears the session down).
    [Fact]
    public async Task AutoTurn_WorkspaceSwitchedWhileJudging_SaysTheMessageWasNotSent()
    {
        var (connection, _) = AutoConnection("medium");
        var factory = new SingleConnectionFactory(connection);
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        var services = new StubChatSessionServices(factory, new AlwaysSignedInAuthService(), "/solution-a")
        {
            EffortClassifier = classifier,
        };
        using var vm = new ChatViewModel(services);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        factory.ConnectHandler = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return connection;
        };
        services.SetWorkspaceRoot("/solution-b");
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);

        Assert.Empty(connection.Prompts);
        Assert.Equal("hard work", vm.InputText);
        Assert.Contains("was not sent", vm.StatusMessage);
    }

    // Auto is suspended, not lost, while the agent does not offer it - a config update without the
    // levels, or a model with no effort setting - and is back when it does.
    [Fact]
    public async Task Auto_ComesBack_WhenTheAgentOffersTheThreeLevelsAgain()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        var withLevels = Options("sonnet", "medium", AdvertisedEfforts);

        connection.RaiseSessionUpdate(new SessionUpdate.ConfigOptionsChanged(Options("sonnet", "xhigh", "xhigh", "max")));
        Assert.DoesNotContain(vm.AvailableEfforts, value => value.Name == "Auto");
        connection.RaiseSessionUpdate(new SessionUpdate.ConfigOptionsChanged(withLevels));
        Assert.Same(Auto(vm), vm.SelectedEffort);

        connection.RaiseSessionUpdate(new SessionUpdate.ConfigOptionsChanged(new[] { withLevels[0] }));
        Assert.False(vm.HasEffort);
        connection.RaiseSessionUpdate(new SessionUpdate.ConfigOptionsChanged(withLevels));
        Assert.Same(Auto(vm), vm.SelectedEffort);

        await SendTextAsync(vm, "git status");
        Assert.Equal(new[] { "git status" }, classifier.Prompts);
        Assert.Equal(new[] { "effort=low", "prompt:git status" }, log);
    }

    // A session lost after the judgment, while the agent applies the level, is lost like one lost
    // during the judgment: nothing is sent and the message comes back to the composer.
    [Fact]
    public async Task AutoTurn_SessionLostWhileSettingEffort_SendsNothingAndReturnsTheDraft()
    {
        var (connection, log) = AutoConnection("medium");
        var release = new TaskCompletionSource();
        connection.ConfigHandler = async (_, _, _) =>
        {
            await release.Task;
            return connection.ConfigOptions;
        };
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => connection.ConfigChanges.Count == 1);
        connection.RaiseDisconnected();
        release.SetResult();
        await WithinAsync(sending);

        Assert.Empty(connection.Prompts);
        Assert.Empty(log);
        Assert.Equal("hard work", vm.InputText);
        Assert.False(vm.IsBusy);
        // The released session's acknowledgement is not applied to the emptied pickers.
        Assert.False(vm.HasEffort);
    }

    // IEffortClassifier is a public contract: a value that is no level is no verdict, the turn falls
    // back like for any failed judgment instead of failing on the way to the agent.
    [Fact]
    public async Task AutoTurn_ClassifierReturnsAValueThatIsNoLevel_IsAFailedJudgment()
    {
        var (connection, log) = AutoConnection("low");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult((EffortLevel)5) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        await SendTextAsync(vm, "hello");

        Assert.Equal(new[] { "effort=high", "prompt:hello" }, log);
        Assert.Contains("unknown level", vm.StatusMessage);
    }
}
