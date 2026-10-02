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
    private static readonly string[] _advertisedEfforts = { "low", "medium", "high", "xhigh", "max" };

    private sealed class FakeEffortClassifier : IEffortClassifier
    {
        /// <summary>
        /// Gets the collection of prompts.
        /// </summary>
        public List<string> Prompts { get; } = [];
        /// <summary>
        /// Gets the collection of tokens.
        /// </summary>
        public List<CancellationToken> Tokens { get; } = [];
        /// <summary>
        /// Gets or sets the handler.
        /// </summary>
        public Func<string, Task<EffortLevel>> Handler { get; set; } = _ => Task.FromResult(EffortLevel.Medium);
        /// <summary>
        /// Gets or sets the token handler.
        /// </summary>
        public Func<string, CancellationToken, Task<EffortLevel>>? TokenHandler { get; set; }

        public Task<EffortLevel> ClassifyAsync(string prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            Tokens.Add(cancellationToken);
            return TokenHandler?.Invoke(prompt, cancellationToken) ?? Handler(prompt);
        }
    }

    private static (RecordingAcpAgentConnection Connection, List<string> Log) AutoConnection(string effort = "medium")
    {
        var log = new List<string>();
        var current = effort;
        var connection = new RecordingAcpAgentConnection { ConfigOptions = Options("sonnet", effort, _advertisedEfforts) };
        connection.ConfigHandler = (_, value, _) =>
        {
            log.Add("effort=" + value);
            current = value;
            return Task.FromResult(Options("sonnet", current, _advertisedEfforts));
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
        Assert.Equal("Auto · high", vm.ActiveEffortName);
    }

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
        Assert.Contains("Auto effort could not set the effort to high", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

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
        Assert.Contains("rejected", vm.StatusMessage, StringComparison.Ordinal);
        Assert.EndsWith("Your queued message was not sent because the message before it failed - it is back in the message box, after that one.", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoTurn_DraftFailsBeforeItIsSent_SeveralQueuedFollowUps_ComeBackInOrder_AndAreCounted()
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
        await SendTextAsync(vm, "third");
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Empty(log);
        Assert.Equal(string.Join(Environment.NewLine + Environment.NewLine, "first", "second", "third"), vm.InputText);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User);
        Assert.EndsWith("2 queued messages were not sent because the message before them failed - they are back in the message box, after it.", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoTurn_DraftFailsBeforeItIsSent_InAChatWithItsOwnTitle_KeepsTheTitle()
    {
        var (connection, _) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        await vm.OpenSessionAsync(new SessionSummary("session-2", "/workspace", "Older chat", null));
        Assert.Equal("Older chat", vm.SessionTitle);
        connection.ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"));

        await SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal("hard work", vm.InputText);
        Assert.Equal("Older chat", vm.SessionTitle);
    }

    [Fact]
    public async Task AutoTurn_DraftFailsBeforeItIsSent_ObserverThrowsAsTheFollowUpLeaves_BothStayInTheComposer()
    {
        var (connection, _) = AutoConnection("medium");
        connection.ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"));
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "second");
        vm.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
                e.OldItems!.Cast<ChatMessageViewModel>().Any(message => message.Text == "second"))
            {
                throw new InvalidOperationException("observer failed");
            }
        };
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Empty(connection.Prompts);
        Assert.Equal("first" + Environment.NewLine + Environment.NewLine + "second", vm.InputText);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User);
        Assert.Contains("rejected", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoTurn_PlanReviewGivenBack_KeepsTheDraftHeldAsideForIt()
    {
        var (connection, _) = AutoConnection("medium");
        var firstTurn = new TaskCompletionSource();
        connection.PromptHandler = _ => firstTurn.Task;
        connection.ConfigHandler = (_, value, _) => value == "high"
            ? Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"))
            : Task.FromResult(Options("sonnet", value, _advertisedEfforts));
        var classifier = new FakeEffortClassifier
        {
            Handler = prompt => Task.FromResult(prompt.StartsWith("Review comments", StringComparison.Ordinal) ? EffortLevel.High : EffortLevel.Low),
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        vm.InputText = "plan the feature";
        var sending = vm.SendAsync();
        await WaitUntilAsync(() => connection.Prompts.Count == 1);
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        vm.InputText = "half-written idea";
        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");
        firstTurn.SetResult();
        await WithinAsync(sending);
        await WaitUntilAsync(() => classifier.Prompts.Count == 2);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Single(connection.Prompts);
        Assert.Equal("Review comments on the plan:\nAdd a rollback step." + Environment.NewLine + Environment.NewLine + "half-written idea", vm.InputText);
    }

    [Fact]
    public async Task AutoTurn_StoppedWhileJudging_ObserverThrowsAsTheMessageLeaves_TheQueuedFollowUpStillGoesOut()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier
        {
            Handler = prompt => prompt == "first" ? verdict.Task : Task.FromResult(EffortLevel.Medium),
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "second");
        vm.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
                e.OldItems!.Cast<ChatMessageViewModel>().Any(message => message.Text == "first"))
            {
                throw new InvalidOperationException("observer failed");
            }
        };
        await vm.CancelAsync();
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => log.Contains("prompt:second"));

        Assert.DoesNotContain("prompt:first", log);
        Assert.Equal("first", vm.InputText);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoTurn_DraftFailsBeforeItIsSent_ObserverThrowsAsTheDraftLeaves_TheFollowUpsBubbleLeavesToo()
    {
        var (connection, _) = AutoConnection("medium");
        connection.ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"));
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "second");
        vm.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
                e.OldItems!.Cast<ChatMessageViewModel>().Any(message => message.Text == "first"))
            {
                throw new InvalidOperationException("observer failed");
            }
        };
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Equal("first" + Environment.NewLine + Environment.NewLine + "second", vm.InputText);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User);
    }

    [Fact]
    public async Task AutoTurn_StoppedWhileJudging_ObserverThrowsAsTheMessageLeaves_TheHeldBackReviewStillGoesOut()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier
        {
            Handler = prompt => prompt == "first" ? verdict.Task : Task.FromResult(EffortLevel.Medium),
        };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "first");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");
        vm.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
                e.OldItems!.Cast<ChatMessageViewModel>().Any(message => message.Text == "first"))
            {
                throw new InvalidOperationException("observer failed");
            }
        };
        await vm.CancelAsync();
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => log.Any(entry => entry.StartsWith("prompt:Review comments", StringComparison.Ordinal)));

        Assert.DoesNotContain("prompt:first", log);
        Assert.Contains("observer failed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanReviewGivenBack_ObserverThrowsAsItLeavesTheTranscript_ReportsIt()
    {
        var (connection, _) = AutoConnection("medium");
        connection.ConfigHandler = (_, _, _) => Task.FromException<IReadOnlyList<SessionConfigOption>>(new InvalidOperationException("rejected"));
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.High) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        vm.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
            {
                throw new InvalidOperationException("observer failed");
            }
        };

        var (call, options) = PlanApprovalRequest();
        connection.RaisePermissionRequested(call, options);
        vm.PendingPlan!.ReviewCommand.Execute("Add a rollback step.");
        await WaitUntilAsync(() => vm.StatusMessage?.Contains("observer failed", StringComparison.Ordinal) == true);

        Assert.False(vm.IsBusy);
        Assert.Empty(connection.Prompts);
        Assert.StartsWith("Review comments on the plan:", vm.InputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoTurn_StoppedWhileJudging_AfterAReplyArrived_LeavesTheChatUntitled()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        connection.RaiseSessionUpdate(new SessionUpdate.AgentMessageChunk("late reply"));
        Assert.Equal("hard work", vm.SessionTitle);
        await vm.CancelAsync();
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Empty(log);
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
        Assert.Equal("Your message is back in the message box, together with what was already there.", vm.StatusMessage);
    }

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
            if (calls++ == call)
            {
                await release.Task;
            }

            return await acknowledgement;
        };
        return (release, tokens);
    }

    private static async Task StopOnceRequestedAsync(ChatViewModel vm, RecordingAcpAgentConnection connection, int requests = 1)
    {
        await WaitUntilAsync(() => connection.ConfigChanges.Count == requests);
        await WithinAsync(vm.CancelAsync());
    }

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

    [Fact]
    public async Task AutoTurn_JudgeFailed_ThenStoppedWhileSettingEffort_StillSaysWhereTheMessageWent()
    {
        var (connection, _) = AutoConnection("medium");
        var (release, _) = StallEffortRequest(connection);
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromException<EffortLevel>(new InvalidOperationException("judge down")) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => connection.ConfigChanges.Count > 0);
        Assert.Contains("judge down", vm.StatusMessage, StringComparison.Ordinal);
        vm.InputText = "typed since";
        await StopOnceRequestedAsync(vm, connection);
        release.SetResult();
        await WithinAsync(sending);

        Assert.Empty(connection.Prompts);
        Assert.Equal("hard work" + Environment.NewLine + Environment.NewLine + "typed since", vm.InputText);
        Assert.EndsWith("Your message is back in the message box, together with what was already there.", vm.StatusMessage, StringComparison.Ordinal);
    }

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
                if (prompt == "second" && judgedSecond++ == 0)
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

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

        vm.Attachments.Add(new ChatAttachmentViewModel("c.png", "image/png", "CCCC"));
        await SendTextAsync(vm, "   ");
        Assert.Equal(2, log.Count);
        Assert.Equal(new[] { "small" }, classifier.Prompts);
    }

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

        Assert.Equal(new[] { "first", "second" }, classifier.Prompts);
        Assert.Equal(new[] { "effort=low", "prompt:first", "effort=high", "prompt:second" }, log);
    }

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

    [Fact]
    public async Task AutoTurn_StoppedWhileJudging_AfterAnotherMessageWasSent_ComesBackWithItsAttachments()
    {
        var (connection, _) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (prompt, token) =>
            {
                if (prompt == "look at this")
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

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
                if (prompt == "second")
                {
                    return await secondVerdict.Task;
                }

                if (judgedHard++ == 0)
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

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
        Assert.Equal("Agent disconnected. The session changed while judging effort, so your message was not sent.", vm.StatusMessage);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AutoTurn_SessionLostWhileJudging_ReturnsOnlyTheJudgedMessage_AndDropsTheQueuedFollowUpWithNotice()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        await SendTextAsync(vm, "follow-up");
        connection.RaiseDisconnected();
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Empty(log);
        Assert.Equal("hard work", vm.InputText);
        Assert.DoesNotContain(vm.Messages, message => message.Role == ChatRole.User);
        Assert.Contains("A queued message was not sent.", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoTurn_SessionLostWhileJudging_AfterTextWasTyped_SaysWhyAndWhereTheMessageWent()
    {
        var (connection, log) = AutoConnection("medium");
        var verdict = new TaskCompletionSource<EffortLevel>();
        var classifier = new FakeEffortClassifier { Handler = _ => verdict.Task };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));

        var sending = SendTextAsync(vm, "hard work");
        await WaitUntilAsync(() => classifier.Prompts.Count == 1);
        vm.InputText = "typed since";
        connection.RaiseDisconnected();
        verdict.SetResult(EffortLevel.High);
        await WithinAsync(sending);

        Assert.Empty(log);
        Assert.Equal("hard work" + Environment.NewLine + Environment.NewLine + "typed since", vm.InputText);
        Assert.Equal("Agent disconnected. The session changed while judging effort, so your message was not sent. Your message is back in the message box, together with what was already there.", vm.StatusMessage);
    }

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

    [Fact]
    public async Task AutoTurn_JudgedDraftStoppedAfterALaterMessageWasQueued_ReturnsToTheComposer()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (prompt, token) =>
            {
                if (prompt == "first")
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

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

    [Fact]
    public async Task AutoTurn_JudgeCancelsItselfAfterAVerdict_KeepsTheLastLevel_SaysAndLogsIt()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier
        {
            TokenHandler = async (prompt, _) =>
            {
                if (prompt == "git push")
                {
                    return EffortLevel.Low;
                }

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
            return Task.FromResult(Options(model, effort, model == "opus" ? opusLevels : _advertisedEfforts));
        };
        return (connection, log);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public async Task ModelSwitch_ToAModelLackingALevel_SuspendsAuto_UntilAModelWithAllThreeReturns(string missing)
    {
        var opusLevels = _advertisedEfforts.Where(level => level != missing).ToArray();
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

    [Fact]
    public async Task ModelSwitch_ToAModelWithAllThreeLevels_KeepsAutoWorking()
    {
        var (connection, log) = ModelSwitchingConnection(_advertisedEfforts, "high");
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

    [Fact]
    public async Task ModelSwitch_MovesTheAgentsEffort_PickerNamesNoLevelAutoDidNotSet()
    {
        var (connection, _) = ModelSwitchingConnection(_advertisedEfforts, "high");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        await SendTextAsync(vm, "git push");
        Assert.Equal("Auto · low", vm.ActiveEffortName);

        await vm.SelectModelAsync(vm.AvailableModels.Single(value => value.Value == "opus"));

        Assert.Equal("Auto", vm.ActiveEffortName);
    }

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
            if (holdRequests)
            {
                await release.Task;
            }

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
            if (holdRequests)
            {
                await release.Task;
            }

            if (value == "xhigh")
            {
                throw new InvalidOperationException("rejected");
            }

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
        Assert.Single(connection.Prompts);

        Assert.Equal("Auto · high", vm.ActiveEffortName);
    }

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

    [Fact]
    public async Task Auto_ComesBack_WhenTheAgentOffersTheThreeLevelsAgain()
    {
        var (connection, log) = AutoConnection("medium");
        var classifier = new FakeEffortClassifier { Handler = _ => Task.FromResult(EffortLevel.Low) };
        using var vm = CreateWithClassifier(connection, classifier);
        await vm.Initialization;
        await vm.SelectEffortAsync(Auto(vm));
        var withLevels = Options("sonnet", "medium", _advertisedEfforts);

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
        Assert.False(vm.HasEffort);
    }

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
