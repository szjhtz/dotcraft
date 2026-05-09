using DotCraft.Abstractions;
using DotCraft.Agents;
using DotCraft.Configuration;
using DotCraft.Context;
using DotCraft.Memory;
using DotCraft.Protocol;
using DotCraft.Security;
using DotCraft.Sessions;
using DotCraft.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Sessions.Protocol;

public sealed class SessionServiceMemoryConsolidationTests : IDisposable
{
    private readonly string _tempDir;

    public SessionServiceMemoryConsolidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MemoryConsolidation_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task SubmitInputAsync_WhenConsolidationIsSkipped_EmitsSkippedWithoutConsolidated()
    {
        var consolidator = new FakeMemoryConsolidator(MemoryConsolidationResult.Skipped("save_memory_not_called"));
        var chatClient = new StaticChatClient("ok");
        await using var agentFactory = CreateAgentFactory(chatClient, consolidator);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        var subscription = CollectThreadEventsAsync(
            svc,
            thread.Id,
            events => events.Any(IsConsolidationTerminal));

        var turnEvents = await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("remember blue")]));
        var threadEvents = await subscription;

        Assert.Contains(turnEvents, e => IsSystemEvent(e, "consolidating"));
        Assert.Contains(threadEvents, e => IsSystemEvent(e, "consolidationSkipped"));
        Assert.DoesNotContain(threadEvents, e => IsSystemEvent(e, "consolidated"));
    }

    [Fact]
    public async Task SubmitInputAsync_WhenConsolidationSucceeds_EmitsConsolidatedAndPersistentNotice()
    {
        var startSawPersistedTurn = false;
        var threadStore = new ThreadStore(_tempDir);
        string? completedThreadId = null;
        var consolidator = new FakeMemoryConsolidator(
            MemoryConsolidationResult.Succeeded(memoryWritten: true, historyWritten: true),
            async () =>
            {
                var persisted = completedThreadId == null
                    ? null
                    : await threadStore.LoadThreadAsync(completedThreadId);
                startSawPersistedTurn = persisted?.Turns.SingleOrDefault()?.Status == TurnStatus.Completed;
            });
        var chatClient = new StaticChatClient("ok");
        await using var agentFactory = CreateAgentFactory(chatClient, consolidator);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());
        var runtimeSignals = new List<SessionThreadRuntimeSignal>();
        svc.ThreadRuntimeSignalForBroadcast = (threadId, signal) =>
        {
            if (threadId == thread.Id)
                runtimeSignals.Add(signal);
        };
        completedThreadId = thread.Id;

        var subscription = CollectThreadEventsAsync(
            svc,
            thread.Id,
            events => events.Any(e => IsSystemEvent(e, "consolidated"))
                && events.Any(e => IsMemoryNotice(e)));

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("remember blue")]));
        var threadEvents = await subscription;

        Assert.Contains(threadEvents, e => IsSystemEvent(e, "consolidated"));
        Assert.Contains(threadEvents, IsMemoryNotice);
        Assert.True(startSawPersistedTurn);
        Assert.Contains(SessionThreadRuntimeSignal.MemoryConsolidated, runtimeSignals);

        var reloaded = await svc.GetThreadAsync(thread.Id);
        var notice = Assert.Single(reloaded.Turns.Single().Items, item => item.Type == ItemType.SystemNotice);
        Assert.Equal("memoryConsolidated", notice.AsSystemNotice?.Kind);
    }

    [Theory]
    [InlineData(MemoryConsolidationOutcome.Skipped)]
    [InlineData(MemoryConsolidationOutcome.Failed)]
    public async Task SubmitInputAsync_WhenConsolidationDoesNotWriteMemory_DoesNotEmitMemoryConsolidatedSignal(
        MemoryConsolidationOutcome outcome)
    {
        var result = outcome == MemoryConsolidationOutcome.Skipped
            ? MemoryConsolidationResult.Skipped("no changes")
            : MemoryConsolidationResult.Failed("provider unavailable");
        var consolidator = new FakeMemoryConsolidator(result);
        var chatClient = new StaticChatClient("ok");
        await using var agentFactory = CreateAgentFactory(chatClient, consolidator);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());
        var runtimeSignals = new List<SessionThreadRuntimeSignal>();
        svc.ThreadRuntimeSignalForBroadcast = (threadId, signal) =>
        {
            if (threadId == thread.Id)
                runtimeSignals.Add(signal);
        };

        var subscription = CollectThreadEventsAsync(
            svc,
            thread.Id,
            events => events.Any(IsConsolidationTerminal));

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("remember blue")]));
        await subscription;

        Assert.DoesNotContain(SessionThreadRuntimeSignal.MemoryConsolidated, runtimeSignals);
    }

    [Fact]
    public async Task SubmitInputAsync_WhenConsolidationFails_EmitsFailedWithoutNotice()
    {
        var consolidator = new FakeMemoryConsolidator(MemoryConsolidationResult.Failed("provider unavailable"));
        var chatClient = new StaticChatClient("ok");
        await using var agentFactory = CreateAgentFactory(chatClient, consolidator);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        var subscription = CollectThreadEventsAsync(
            svc,
            thread.Id,
            events => events.Any(IsConsolidationTerminal));

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("remember blue")]));
        var threadEvents = await subscription;

        Assert.Contains(threadEvents, e => IsSystemEvent(e, "consolidationFailed"));
        Assert.DoesNotContain(threadEvents, IsMemoryNotice);
    }

    [Fact]
    public async Task ConsolidateThreadMemoryAsync_WhenSuccessful_EmitsEventsPersistsNoticeAndReturnsFlags()
    {
        var consolidator = new FakeMemoryConsolidator(
            MemoryConsolidationResult.Succeeded(memoryWritten: true, historyWritten: true));
        var chatClient = new StaticChatClient("ok");
        await using var agentFactory = CreateAgentFactory(
            chatClient,
            consolidator,
            config => config.Memory.AutoConsolidateEnabled = false);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("remember blue")]));

        var result = await svc.ConsolidateThreadMemoryAsync(thread.Id);
        var threadEvents = await CollectThreadEventsAsync(
            svc,
            thread.Id,
            events => events.Any(e => IsSystemEvent(e, "consolidated"))
                && events.Any(IsMemoryNotice));

        Assert.Equal("succeeded", result.Outcome);
        Assert.True(result.MemoryWritten);
        Assert.True(result.HistoryWritten);
        Assert.Contains(threadEvents, e => IsSystemEvent(e, "consolidating"));
        Assert.Contains(threadEvents, e => IsSystemEvent(e, "consolidated"));
        Assert.Contains(threadEvents, IsMemoryNotice);

        var reloaded = await svc.GetThreadAsync(thread.Id);
        var notice = Assert.Single(reloaded.Turns.Single().Items, item => item.Type == ItemType.SystemNotice);
        Assert.Equal("memoryConsolidated", notice.AsSystemNotice?.Kind);
    }

    [Theory]
    [InlineData(MemoryConsolidationOutcome.Skipped, "no changes", "consolidationSkipped")]
    [InlineData(MemoryConsolidationOutcome.Failed, "provider unavailable", "consolidationFailed")]
    public async Task ConsolidateThreadMemoryAsync_WhenNotSuccessful_EmitsTerminalEventWithoutNotice(
        MemoryConsolidationOutcome outcome,
        string message,
        string terminalKind)
    {
        var consolidationResult = outcome == MemoryConsolidationOutcome.Skipped
            ? MemoryConsolidationResult.Skipped(message)
            : MemoryConsolidationResult.Failed(message);
        var consolidator = new FakeMemoryConsolidator(consolidationResult);
        var chatClient = new StaticChatClient("ok");
        await using var agentFactory = CreateAgentFactory(
            chatClient,
            consolidator,
            config => config.Memory.AutoConsolidateEnabled = false);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("remember blue")]));

        var result = await svc.ConsolidateThreadMemoryAsync(thread.Id);
        var threadEvents = await CollectThreadEventsAsync(
            svc,
            thread.Id,
            events => events.Any(e => IsSystemEvent(e, terminalKind)));

        Assert.Equal(outcome == MemoryConsolidationOutcome.Skipped ? "skipped" : "failed", result.Outcome);
        Assert.Equal(message, result.Message);
        Assert.Contains(threadEvents, e => IsSystemEvent(e, "consolidating"));
        Assert.Contains(threadEvents, e => IsSystemEvent(e, terminalKind));
        Assert.DoesNotContain(threadEvents, IsMemoryNotice);
    }

    [Fact]
    public async Task ConsolidateThreadMemoryAsync_RejectsEmptyClientManagedAndActiveThreads()
    {
        var consolidator = new FakeMemoryConsolidator(
            MemoryConsolidationResult.Succeeded(memoryWritten: true, historyWritten: true));
        var blockingChat = new BlockingChatClient();
        await using var agentFactory = CreateAgentFactory(
            blockingChat,
            consolidator,
            config => config.Memory.AutoConsolidateEnabled = false);
        var svc = CreateService(agentFactory, blockingChat);

        var empty = await svc.CreateThreadAsync(MakeIdentity(), threadId: "thread-empty");
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConsolidateThreadMemoryAsync(empty.Id));

        var clientManaged = await svc.CreateThreadAsync(
            MakeIdentity(),
            historyMode: HistoryMode.Client,
            threadId: "thread-client");
        clientManaged.Turns.Add(new SessionTurn
        {
            Id = "turn_001",
            ThreadId = clientManaged.Id,
            Status = TurnStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConsolidateThreadMemoryAsync(clientManaged.Id));

        var running = await svc.CreateThreadAsync(MakeIdentity(), threadId: "thread-running");
        _ = Task.Run(async () =>
        {
            await foreach (var _ in svc.SubmitInputAsync(running.Id, [new TextContent("keep running")]))
            {
            }
        });
        await WaitUntilAsync(() => running.Turns.Any(t => t.Status == TurnStatus.Running));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConsolidateThreadMemoryAsync(running.Id));
        blockingChat.Release();
    }

    private SessionService CreateService(AgentFactory agentFactory, IChatClient chatClient)
    {
        var defaultAgent = chatClient.AsAIAgent(new ChatClientAgentOptions());
        return new SessionService(
            agentFactory,
            defaultAgent,
            new SessionPersistenceService(new ThreadStore(_tempDir)),
            new SessionGate());
    }

    private AgentFactory CreateAgentFactory(
        IChatClient chatClient,
        IMemoryConsolidator consolidator,
        Action<AppConfig>? configureConfig = null)
    {
        var config = new AppConfig
        {
            ApiKey = "sk-test-not-used-for-network",
            EndPoint = "https://127.0.0.1:9/v1",
            Memory = { ConsolidateEveryNTurns = 1 }
        };
        configureConfig?.Invoke(config);
        var memory = new MemoryStore(_tempDir);
        var skills = new SkillsLoader(_tempDir);
        return new AgentFactory(
            dotcraftPath: _tempDir,
            workspacePath: _tempDir,
            config: config,
            memoryStore: memory,
            skillsLoader: skills,
            approvalService: new AutoApproveApprovalService(),
            blacklist: null,
            toolProviders: Array.Empty<IAgentToolProvider>(),
            memoryConsolidator: consolidator);
    }

    private SessionIdentity MakeIdentity() => new()
    {
        ChannelName = "test",
        UserId = "u",
        WorkspacePath = _tempDir
    };

    private static async Task<List<SessionEvent>> DrainAsync(IAsyncEnumerable<SessionEvent> events)
    {
        var collected = new List<SessionEvent>();
        await foreach (var evt in events)
            collected.Add(evt);
        return collected;
    }

    private static async Task<List<SessionEvent>> CollectThreadEventsAsync(
        ISessionService svc,
        string threadId,
        Func<List<SessionEvent>, bool> done)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var collected = new List<SessionEvent>();
        await foreach (var evt in svc.SubscribeThreadAsync(threadId, replayRecent: true, cts.Token))
        {
            collected.Add(evt);
            if (done(collected))
                break;
        }

        return collected;
    }

    private static bool IsConsolidationTerminal(SessionEvent evt) =>
        IsSystemEvent(evt, "consolidated")
        || IsSystemEvent(evt, "consolidationSkipped")
        || IsSystemEvent(evt, "consolidationFailed");

    private static bool IsSystemEvent(SessionEvent evt, string kind) =>
        evt.EventType == SessionEventType.SystemEvent
        && evt.Payload is SystemEventPayload payload
        && payload.Kind == kind;

    private static bool IsMemoryNotice(SessionEvent evt) =>
        evt.EventType == SessionEventType.ItemCompleted
        && evt.Payload is SessionItem { Payload: SystemNoticePayload { Kind: "memoryConsolidated" } };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, cts.Token);
        }
    }

    private sealed class FakeMemoryConsolidator(
        MemoryConsolidationResult result,
        Func<Task>? onStart = null) : IMemoryConsolidator
    {
        public async Task<MemoryConsolidationResult> ConsolidateAsync(
            IReadOnlyList<ChatMessage> messagesToArchive,
            CancellationToken cancellationToken = default)
        {
            if (onStart != null)
                await onStart();
            return result;
        }
    }

    private sealed class StaticChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, [new TextContent(responseText)])]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(responseText)]);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class BlockingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await _release.Task.WaitAsync(cancellationToken);
            return new ChatResponse([new ChatMessage(ChatRole.Assistant, [new TextContent("ok")])]);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            Release();
        }
    }
}
