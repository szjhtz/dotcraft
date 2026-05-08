using DotCraft.Abstractions;
using DotCraft.Agents;
using DotCraft.Configuration;
using DotCraft.Context.Compaction;
using DotCraft.Memory;
using DotCraft.Protocol;
using DotCraft.Security;
using DotCraft.Sessions;
using DotCraft.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Sessions.Protocol;

public sealed class SessionServiceManualCompactionTests : IDisposable
{
    private readonly string _tempDir;

    public SessionServiceManualCompactionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ManualCompact_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task CompactThreadAsync_Success_EmitsEventsPersistsNoticeAndUpdatesUsage()
    {
        var mainChat = new StreamingReplyChatClient("ok");
        var summaryChat = new SummaryChatClient("<summary>older context summary</summary>");
        await using var agentFactory = CreateAgentFactory(summaryChat);
        var service = CreateService(agentFactory, mainChat);
        var thread = await service.CreateThreadAsync(MakeIdentity(), threadId: "thread-compact");

        for (var i = 0; i < 4; i++)
        {
            await DrainAsync(service.SubmitInputAsync(
                thread.Id,
                [new TextContent($"turn {i} " + new string('u', 1200))]));
        }

        var result = await service.CompactThreadAsync(thread.Id);
        Assert.True(result.Outcome == "partial", result.Message ?? result.Outcome);
        var events = await CollectThreadEventsAsync(
            service,
            thread.Id,
            replayRecent: true,
            events => events.Any(e => IsSystemEvent(e, "compacted"))
                && events.Any(IsManualCompactionNotice));

        Assert.NotNull(result.ContextUsage);
        Assert.True(result.ContextUsage!.Tokens > 0);
        Assert.Contains(events, e => IsSystemEvent(e, "compacting"));
        Assert.Contains(events, e => IsSystemEvent(e, "compacted"));
        Assert.Contains(events, IsManualCompactionNotice);

        var reloaded = await service.GetThreadAsync(thread.Id);
        var notice = reloaded.Turns
            .SelectMany(t => t.Items)
            .Select(i => i.Payload)
            .OfType<SystemNoticePayload>()
            .Single(p => p.Kind == "compacted" && p.Trigger == "manual");
        Assert.Equal("partial", notice.Mode);
        Assert.Equal(result.ContextUsage.Tokens, notice.TokensAfter);
    }

    [Fact]
    public async Task CompactThreadAsync_ShortHistorySkipsWithoutNotice()
    {
        var mainChat = new StreamingReplyChatClient("ok");
        await using var agentFactory = CreateAgentFactory(new SummaryChatClient("<summary>unused</summary>"));
        var service = CreateService(agentFactory, mainChat);
        var thread = await service.CreateThreadAsync(MakeIdentity(), threadId: "thread-short");

        await DrainAsync(service.SubmitInputAsync(
            thread.Id,
            [new TextContent("single turn " + new string('u', 1200))]));

        var result = await service.CompactThreadAsync(thread.Id);
        Assert.Equal("skipped", result.Outcome);
        Assert.Equal("no_summarizable_prefix", result.Message);

        var events = await CollectThreadEventsAsync(
            service,
            thread.Id,
            replayRecent: true,
            events => events.Any(e => IsSystemEvent(e, "compactSkipped")));

        Assert.Contains(events, e => IsSystemEvent(e, "compacting"));
        Assert.Contains(events, e => IsSystemEvent(e, "compactSkipped")
            && e.Payload is SystemEventPayload { Message: "no_summarizable_prefix" });
        Assert.DoesNotContain(events, IsManualCompactionNotice);

        var reloaded = await service.GetThreadAsync(thread.Id);
        Assert.DoesNotContain(
            reloaded.Turns.SelectMany(t => t.Items).Select(i => i.Payload).OfType<SystemNoticePayload>(),
            p => p.Kind == "compacted" && p.Trigger == "manual");
    }

    [Fact]
    public async Task CompactThreadAsync_RejectsEmptyClientManagedAndActiveThreads()
    {
        var mainChat = new BlockingChatClient();
        await using var agentFactory = CreateAgentFactory(new SummaryChatClient("<summary>unused</summary>"));
        var service = CreateService(agentFactory, mainChat);

        var empty = await service.CreateThreadAsync(MakeIdentity(), threadId: "thread-empty");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactThreadAsync(empty.Id));

        var clientManaged = await service.CreateThreadAsync(
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactThreadAsync(clientManaged.Id));

        var running = await service.CreateThreadAsync(MakeIdentity(), threadId: "thread-running");
        _ = Task.Run(async () =>
        {
            await foreach (var _ in service.SubmitInputAsync(running.Id, [new TextContent("keep running")]))
            {
            }
        });
        await WaitUntilAsync(() => running.Turns.Any(t => t.Status == TurnStatus.Running));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactThreadAsync(running.Id));
        mainChat.Release();
    }

    private SessionService CreateService(AgentFactory agentFactory, IChatClient mainChatClient)
    {
        var defaultAgent = mainChatClient.AsAIAgent(new ChatClientAgentOptions());
        return new SessionService(
            agentFactory,
            defaultAgent,
            new SessionPersistenceService(new ThreadStore(_tempDir)),
            new SessionGate());
    }

    private AgentFactory CreateAgentFactory(IChatClient compactionChatClient)
    {
        var config = new AppConfig
        {
            ApiKey = "sk-test-not-used-for-network",
            EndPoint = "https://127.0.0.1:9/v1",
            Compaction = new CompactionConfig
            {
                ContextWindow = 200_000,
                SummaryReserveTokens = 20_000,
                AutoCompactBufferTokens = 13_000,
                WarningBufferTokens = 20_000,
                ErrorBufferTokens = 10_000,
                KeepRecentMinTokens = 1,
                KeepRecentMinGroups = 1,
                KeepRecentMaxTokens = 1_000,
                MicrocompactEnabled = false
            }
        };
        return new AgentFactory(
            dotcraftPath: _tempDir,
            workspacePath: _tempDir,
            config: config,
            memoryStore: new MemoryStore(_tempDir),
            skillsLoader: new SkillsLoader(_tempDir),
            approvalService: new AutoApproveApprovalService(),
            blacklist: null,
            toolProviders: Array.Empty<IAgentToolProvider>(),
            compactionChatClient: compactionChatClient);
    }

    private SessionIdentity MakeIdentity() => new()
    {
        WorkspacePath = _tempDir,
        ChannelName = "desktop",
        UserId = "user"
    };

    private static async Task DrainAsync(IAsyncEnumerable<SessionEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private static async Task<List<SessionEvent>> CollectThreadEventsAsync(
        ISessionService service,
        string threadId,
        bool replayRecent,
        Func<List<SessionEvent>, bool> done,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var collected = new List<SessionEvent>();
        await foreach (var evt in service.SubscribeThreadAsync(threadId, replayRecent, timeout.Token))
        {
            collected.Add(evt);
            if (done(collected))
                break;
        }

        return collected;
    }

    private static bool IsSystemEvent(SessionEvent evt, string kind) =>
        evt.EventType == SessionEventType.SystemEvent
        && evt.Payload is SystemEventPayload payload
        && payload.Kind == kind;

    private static bool IsManualCompactionNotice(SessionEvent evt) =>
        evt.EventType == SessionEventType.ItemCompleted
        && evt.Payload is SessionItem { Payload: SystemNoticePayload { Kind: "compacted", Trigger: "manual" } };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    private sealed class StreamingReplyChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(responseText)]);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class SummaryChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class BlockingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _gate = new();

        public void Release() => _gate.TrySetResult();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => _gate.TrySetCanceled(cancellationToken));
            await _gate.Task;
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => _gate.TrySetCanceled();
    }
}
