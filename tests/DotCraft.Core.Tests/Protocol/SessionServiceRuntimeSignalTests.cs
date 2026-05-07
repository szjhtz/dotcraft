using System.Text.Json;
using DotCraft.Abstractions;
using DotCraft.Agents;
using DotCraft.Configuration;
using DotCraft.Context;
using DotCraft.Memory;
using DotCraft.Protocol;
using DotCraft.Security;
using DotCraft.Sessions;
using DotCraft.Skills;
using DotCraft.Tools;
using DotCraft.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Sessions.Protocol;

/// <summary>
/// Verifies that <see cref="SessionService"/> emits runtime broadcast signals for turn lifecycle transitions.
/// </summary>
public sealed class SessionServiceRuntimeSignalTests : IDisposable
{
    private readonly string _tempDir;

    public SessionServiceRuntimeSignalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RuntimeSignal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task SubmitInputAsync_EmitsTurnStartedThenCompleted()
    {
        IChatClient chatClient = new FakeChatClient([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());
        var seen = new List<SessionThreadRuntimeSignal>();
        svc.ThreadRuntimeSignalForBroadcast = (threadId, signal) =>
        {
            if (threadId == thread.Id)
                seen.Add(signal);
        };

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("hello")]));

        Assert.Equal(
            [SessionThreadRuntimeSignal.TurnStarted, SessionThreadRuntimeSignal.TurnCompleted],
            seen);
    }

    [Fact]
    public async Task SubmitInputAsync_WhenAgentThrows_EmitsTurnStartedThenFailed()
    {
        IChatClient chatClient = new ThrowingChatClient(new InvalidOperationException("boom"));
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());
        var seen = new List<SessionThreadRuntimeSignal>();
        svc.ThreadRuntimeSignalForBroadcast = (threadId, signal) =>
        {
            if (threadId == thread.Id)
                seen.Add(signal);
        };

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("hello")]));

        Assert.Equal(
            [SessionThreadRuntimeSignal.TurnStarted, SessionThreadRuntimeSignal.TurnFailed],
            seen);
    }

    [Fact]
    public async Task SubmitInputAsync_PassesCapturedPromptRequestSnapshotToMemoryForkConsolidator()
    {
        IChatClient chatClient = new FakeChatClient([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);
        var consolidator = new CapturingForkConsolidator();
        await using var agentFactory = CreateAgentFactory(
            chatClient,
            configureConfig: config => config.Memory.ConsolidateEveryNTurns = 1,
            memoryConsolidator: consolidator);
        var defaultAgent = new StreamingFunctionInvokingChatClient(chatClient).AsAIAgent(
            new ChatClientAgentOptions
            {
                UseProvidedChatClientAsIs = true,
                ChatOptions = new ChatOptions
                {
                    Instructions = "stable base",
                    ModelId = "gpt-test"
                }
            });
        var svc = new SessionService(
            agentFactory,
            defaultAgent,
            new SessionPersistenceService(new ThreadStore(_tempDir)),
            new SessionGate());
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("hello")]));
        var snapshot = await consolidator.WaitForSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(thread.Id, snapshot.ThreadId);
        Assert.Equal("agent", snapshot.Mode);
        Assert.Equal("stable base", snapshot.BaseInstructions);
        Assert.Equal("gpt-test", snapshot.ModelId);
        Assert.Contains(snapshot.Messages, message => message.Role == ChatRole.User && message.Text.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmitInputAsync_SubAgentJsonStringResult_PersistsSuccessfulToolResult()
    {
        const string resultJson = "{\"childThreadId\":\"thread_child\",\"status\":\"running\",\"profileName\":\"native\"}";
        IChatClient chatClient = new FakeChatClient([
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionResultContent("call-1", resultJson)])
        ]);
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("hello")]));

        var loaded = await new ThreadStore(_tempDir).LoadThreadAsync(thread.Id);
        var turn = Assert.Single(loaded!.Turns);
        var resultItem = Assert.Single(turn.Items, item => item.Type == ItemType.ToolResult);
        var payload = Assert.IsType<ToolResultPayload>(resultItem.Payload);
        Assert.True(payload.Success);
        Assert.Equal("call-1", payload.CallId);
        Assert.Equal(resultJson, payload.Result);
        using var doc = JsonDocument.Parse(payload.Result);
        Assert.Equal("thread_child", doc.RootElement.GetProperty("childThreadId").GetString());
    }

    [Fact]
    public async Task SubmitInputAsync_SpawnAgentArgumentsDelta_CompletesSameToolCallItem()
    {
        IChatClient chatClient = new FakeChatClient([
            new ChatResponseUpdate(ChatRole.Assistant, [new ToolCallArgumentsDeltaContent
            {
                ToolCallIndex = 0,
                ToolName = "SpawnAgent",
                CallId = "call-spawn",
                ArgumentsDelta = "{\"agentPrompt\":\"Inspect tests"
            }]),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(
                callId: "call-spawn",
                name: "SpawnAgent",
                arguments: new Dictionary<string, object?>
                {
                    ["agentPrompt"] = "Inspect tests",
                    ["agentNickname"] = "tester"
                })]),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionResultContent(
                "call-spawn",
                "{\"childThreadId\":\"thread_child\",\"status\":\"running\"}")])
        ]);
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        var events = await CollectAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("hello")]));

        var deltaEvent = Assert.Single(events, e => e.ToolCallArgumentsDeltaPayload != null);
        Assert.Equal("SpawnAgent", deltaEvent.ToolCallArgumentsDeltaPayload!.ToolName);
        Assert.Equal("call-spawn", deltaEvent.ToolCallArgumentsDeltaPayload.CallId);

        var completedToolCall = Assert.Single(
            events,
            e => e.EventType == SessionEventType.ItemCompleted
                && e.ItemPayload?.Type == ItemType.ToolCall
                && e.ItemPayload.Payload is ToolCallPayload { ToolName: "SpawnAgent" });
        Assert.Equal(deltaEvent.ItemId, completedToolCall.ItemId);
        var payload = Assert.IsType<ToolCallPayload>(completedToolCall.ItemPayload!.Payload);
        Assert.Equal("tester", payload.Arguments?["agentNickname"]?.ToString());
    }

    [Fact]
    public async Task SubmitInputAsync_ReasoningAroundTool_PersistsSeparateReasoningItems()
    {
        IChatClient chatClient = new FakeChatClient([
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("I need ")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("a tool")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new ToolCallArgumentsDeltaContent
            {
                ToolCallIndex = 0,
                ToolName = "ExampleTool",
                CallId = "call-1",
                ArgumentsDelta = "{\"path\":\"a.txt\"}"
            }]),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(
                callId: "call-1",
                name: "ExampleTool",
                arguments: new Dictionary<string, object?> { ["path"] = "a.txt" })]),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionResultContent("call-1", "tool result")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Now ")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("answer")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])
        ]);
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("hello")]));

        var loaded = await new ThreadStore(_tempDir).LoadThreadAsync(thread.Id);
        var turn = Assert.Single(loaded!.Turns);
        var assistantItems = turn.Items.Where(item => item.Type != ItemType.UserMessage).ToList();

        Assert.Equal(
            [
                ItemType.ReasoningContent,
                ItemType.ToolCall,
                ItemType.ToolResult,
                ItemType.ReasoningContent,
                ItemType.AgentMessage
            ],
            assistantItems.Select(item => item.Type));

        Assert.Equal("I need a tool", Assert.IsType<ReasoningContentPayload>(assistantItems[0].Payload).Text);
        Assert.Equal("Now answer", Assert.IsType<ReasoningContentPayload>(assistantItems[3].Payload).Text);
        Assert.Equal("done", Assert.IsType<AgentMessagePayload>(assistantItems[4].Payload).Text);
        Assert.All(
            assistantItems.Where(item => item.Type == ItemType.ReasoningContent),
            item => Assert.NotNull(item.CompletedAt));
    }

    [Fact]
    public async Task SubmitInputAsync_ReasoningBeforeAgentMessage_FinalizesBeforeMessage()
    {
        IChatClient chatClient = new FakeChatClient([
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("First ")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thought")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("answer")])
        ]);
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        await DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("hello")]));

        var loaded = await new ThreadStore(_tempDir).LoadThreadAsync(thread.Id);
        var turn = Assert.Single(loaded!.Turns);
        var assistantItems = turn.Items.Where(item => item.Type != ItemType.UserMessage).ToList();

        Assert.Equal([ItemType.ReasoningContent, ItemType.AgentMessage], assistantItems.Select(item => item.Type));
        Assert.Equal("First thought", Assert.IsType<ReasoningContentPayload>(assistantItems[0].Payload).Text);
        Assert.Equal("answer", Assert.IsType<AgentMessagePayload>(assistantItems[1].Payload).Text);
        Assert.NotNull(assistantItems[0].CompletedAt);
    }

    [Fact]
    public async Task SubAgentEdgeChanges_InvokeGraphChangedBroadcastHook()
    {
        IChatClient chatClient = new FakeChatClient([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var parent = await svc.CreateThreadAsync(MakeIdentity(), threadId: "parent-1");
        var child = await svc.CreateThreadAsync(
            new SessionIdentity
            {
                ChannelName = SubAgentThreadOrigin.ChannelName,
                UserId = "u",
                ChannelContext = parent.Id,
                WorkspacePath = _tempDir
            },
            threadId: "child-1",
            source: ThreadSource.ForSubAgent(new SubAgentThreadSource
            {
                ParentThreadId = parent.Id,
                RootThreadId = parent.Id,
                Depth = 1
            }));
        var seen = new List<(string parentThreadId, string childThreadId)>();
        svc.SubAgentGraphChangedForBroadcast = (parentThreadId, childThreadId) =>
            seen.Add((parentThreadId, childThreadId));

        await svc.UpsertThreadSpawnEdgeAsync(new ThreadSpawnEdge
        {
            ParentThreadId = parent.Id,
            ChildThreadId = child.Id,
            Status = ThreadSpawnEdgeStatus.Open
        });
        await svc.SetThreadSpawnEdgeStatusAsync(
            parent.Id,
            child.Id,
            ThreadSpawnEdgeStatus.Closed);

        Assert.Equal(
            [("parent-1", "child-1"), ("parent-1", "child-1")],
            seen);
    }

    [Fact]
    public async Task CreateThreadAsync_TopLevelThread_SetsAgentControlToolsFullInToolContext()
    {
        IChatClient chatClient = new FakeChatClient([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);
        var recorder = new RecordingToolProvider();
        await using var agentFactory = CreateAgentFactory(chatClient, [recorder]);
        var svc = CreateService(agentFactory, chatClient);

        var thread = await svc.CreateThreadAsync(
            MakeIdentity(),
            config: new ThreadConfiguration(),
            threadId: "top-policy");

        var seen = Assert.Single(recorder.Contexts, context => context.CurrentThreadId == thread.Id);
        Assert.Equal(AgentControlToolAccess.Full, seen.AgentControlToolAccess);
    }

    [Fact]
    public async Task CreateThreadAsync_SubAgentThread_SetsAgentControlToolsDisabledInToolContext()
    {
        IChatClient chatClient = new FakeChatClient([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);
        var recorder = new RecordingToolProvider();
        await using var agentFactory = CreateAgentFactory(chatClient, [recorder]);
        var svc = CreateService(agentFactory, chatClient);

        var child = await svc.CreateThreadAsync(
            new SessionIdentity
            {
                ChannelName = SubAgentThreadOrigin.ChannelName,
                UserId = "u",
                ChannelContext = "parent-policy",
                WorkspacePath = _tempDir
            },
            config: new ThreadConfiguration(),
            threadId: "child-policy",
            source: ThreadSource.ForSubAgent(new SubAgentThreadSource
            {
                ParentThreadId = "parent-policy",
                RootThreadId = "parent-policy",
                Depth = 1
            }));

        var seen = Assert.Single(recorder.Contexts, context => context.CurrentThreadId == child.Id);
        Assert.Equal(AgentControlToolAccess.Disabled, seen.AgentControlToolAccess);
    }

    [Fact]
    public async Task SubmitInputAsync_RecordsServerManagedUsage_InTokenUsageStore()
    {
        IChatClient chatClient = new FakeChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]),
            UsageUpdate(requestIndex: 1, input: 12_000, output: 1, cachedInput: 8_000),
            UsageUpdate(requestIndex: 2, input: 20_000, output: 2, cachedInput: 18_000),
            UsageUpdate(requestIndex: 3, input: 41_000, output: 8, cachedInput: 40_000)
        ]);
        await using var agentFactory = CreateAgentFactory(chatClient);
        var tokenUsageStore = new TokenUsageStore(_tempDir);
        var svc = CreateService(agentFactory, chatClient, tokenUsageStore);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        await DrainAsync(svc.SubmitInputAsync(
            thread.Id,
            [new TextContent("hello")],
            new SenderContext
            {
                SenderId = "user-42",
                SenderName = "Alice",
                GroupId = "group-9"
            }));

        var summary = Assert.Single(tokenUsageStore.GetSourceSummaries());
        Assert.Equal("test", summary.SourceId);
        Assert.Equal(TokenUsageSourceModes.ServerManaged, summary.SourceMode);
        Assert.Equal(TokenUsageSubjectKinds.User, summary.SubjectKind);
        Assert.Equal(TokenUsageContextKinds.Group, summary.ContextKind);
        Assert.Equal(73_000, summary.TotalInputTokens);
        Assert.Equal(66_000, summary.TotalCachedInputTokens);
        Assert.Equal(7_000, summary.TotalFreshInputTokens);
        Assert.Equal(11, summary.TotalOutputTokens);
        Assert.Equal(73_011, summary.TotalTokens);
        Assert.Equal(3, summary.LlmCallCount);

        var contextUsage = svc.TryGetContextUsageSnapshot(thread.Id);
        Assert.Equal(41_000, contextUsage?.Tokens);

        var subject = Assert.Single(tokenUsageStore.GetSubjectBreakdown("test"));
        Assert.Equal("user-42", subject.Id);
        Assert.Equal("Alice", subject.Label);
        Assert.Equal(73_011, subject.TotalTokens);

        var context = Assert.Single(tokenUsageStore.GetContextBreakdown("test"));
        Assert.Equal("group-9", context.Id);
        Assert.Equal("group-9", context.Label);
        Assert.Equal(1, context.RelatedSubjectCount);
    }

    private static ChatResponseUpdate UsageUpdate(int requestIndex, long input, long output, long cachedInput)
        => new()
        {
            Role = ChatRole.Assistant,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [TokenUsageRequestMetadata.RequestIndexKey] = requestIndex
            },
            Contents =
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = input,
                    OutputTokenCount = output,
                    CachedInputTokenCount = cachedInput
                })
            ]
        };

    [Fact]
    public async Task SubmitInputAsync_AppendsSenderRuntimeContext_AndPersistsInitiator()
    {
        var chatClient = new RecordingChatClient("ok");
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(new SessionIdentity
        {
            ChannelName = "qq",
            UserId = "10001",
            ChannelContext = "group:123456",
            WorkspacePath = _tempDir
        });

        await DrainAsync(svc.SubmitInputAsync(
            thread.Id,
            [new TextContent("hello")],
            new SenderContext
            {
                SenderId = "10001",
                SenderName = "Alice",
                SenderRole = "admin",
                GroupId = "123456"
            }));

        var userMessage = Assert.Single(chatClient.LastMessages, message => message.Role == ChatRole.User);
        var modelInput = string.Concat(userMessage.Contents.OfType<TextContent>().Select(content => content.Text));
        Assert.Contains("<system-reminder>", modelInput);
        Assert.Contains("## Runtime Context", modelInput);
        Assert.Contains("CurrentMode: Agent", modelInput);
        Assert.Contains("Channel: qq", modelInput);
        Assert.Contains("ChannelContext: group:123456", modelInput);
        Assert.Contains("SenderName: Alice", modelInput);

        var persistedThread = await svc.GetThreadAsync(thread.Id);
        var turn = Assert.Single(persistedThread.Turns);
        Assert.Equal("qq", turn.Initiator?.ChannelName);
        Assert.Equal("10001", turn.Initiator?.UserId);
        Assert.Equal("Alice", turn.Initiator?.UserName);
        Assert.Equal("group:123456", turn.Initiator?.ChannelContext);
        Assert.Equal("123456", turn.Initiator?.GroupId);

        Assert.NotNull(turn.Input);
        var payload = turn.Input!.AsUserMessage;
        Assert.NotNull(payload);
        Assert.Equal("10001", payload!.SenderId);
        Assert.Equal("Alice", payload.SenderName);
        Assert.Equal("admin", payload.SenderRole);
        Assert.Equal("group:123456", payload.ChannelContext);
        Assert.Equal("123456", payload.GroupId);
    }

    [Fact]
    public async Task CancelTurnAsync_EmitsTurnStartedThenCancelled()
    {
        IChatClient chatClient = new BlockingChatClient();
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());
        var seen = new List<SessionThreadRuntimeSignal>();
        svc.ThreadRuntimeSignalForBroadcast = (threadId, signal) =>
        {
            if (threadId == thread.Id)
                seen.Add(signal);
        };

        var drainTask = DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("hello")]));
        await Task.Delay(50);
        await svc.CancelTurnAsync(thread.Id, "turn_001");
        await drainTask;

        Assert.Equal(
            [SessionThreadRuntimeSignal.TurnStarted, SessionThreadRuntimeSignal.TurnCancelled],
            seen);
    }

    [Fact]
    public async Task DeleteThreadPermanentlyAsync_DuringRunningTurn_DoesNotRecreateThreadArtifacts()
    {
        IChatClient chatClient = new BlockingChatClient();
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());

        var drainTask = DrainAsync(svc.SubmitInputAsync(thread.Id, [new TextContent("hello")]));
        await Task.Delay(50);

        await svc.DeleteThreadPermanentlyAsync(thread.Id);
        await drainTask;

        var store = new ThreadStore(_tempDir);
        Assert.Null(await store.LoadThreadAsync(thread.Id));
        Assert.DoesNotContain(await store.LoadIndexAsync(), summary => summary.Id == thread.Id);
        Assert.False(File.Exists(Path.Combine(_tempDir, "threads", "active", $"{thread.Id}.jsonl")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "threads", "archived", $"{thread.Id}.jsonl")));
    }

    [Fact]
    public async Task SubmitInputAsync_FirstCompletedTurn_PersistsThreadBeforeSessionRow()
    {
        var firstChatClient = new RecordingChatClient("first answer");
        await using var firstFactory = CreateAgentFactory(firstChatClient);
        var firstService = CreateService(firstFactory, firstChatClient);
        var thread = await firstService.CreateThreadAsync(MakeIdentity());

        await DrainAsync(firstService.SubmitInputAsync(thread.Id, [new TextContent("hello")]));

        var store = new ThreadStore(_tempDir);
        Assert.NotNull(await store.LoadThreadAsync(thread.Id));
        Assert.True(store.SessionFileExists(thread.Id));
        Assert.True(ThreadRowExists(thread.Id));
    }

    [Fact]
    public async Task SubmitInputAsync_AcrossFreshServiceInstance_RestoresPriorConversation()
    {
        var firstChatClient = new RecordingChatClient("first answer");
        await using var firstFactory = CreateAgentFactory(firstChatClient);
        var firstService = CreateService(firstFactory, firstChatClient);
        var thread = await firstService.CreateThreadAsync(MakeIdentity());

        await DrainAsync(firstService.SubmitInputAsync(thread.Id, [new TextContent("hello")]));

        var secondChatClient = new RecordingChatClient("second answer");
        await using var secondFactory = CreateAgentFactory(secondChatClient);
        var secondService = CreateService(secondFactory, secondChatClient);
        await secondService.ResumeThreadAsync(thread.Id);

        await DrainAsync(secondService.SubmitInputAsync(thread.Id, [new TextContent("follow up")]));

        Assert.Equal(
            ["user:hello", "assistant:first answer", "user:follow up"],
            secondChatClient.LastMessages.Select(FormatMessage).ToList());
    }

    [Fact]
    public async Task SubmitInputAsync_WhenLegacyThreadSessionRowIsMissing_RebuildsHistoryFromRollout()
    {
        var firstChatClient = new RecordingChatClient("first answer");
        await using var firstFactory = CreateAgentFactory(firstChatClient);
        var firstService = CreateService(firstFactory, firstChatClient);
        var thread = await firstService.CreateThreadAsync(MakeIdentity());

        await DrainAsync(firstService.SubmitInputAsync(thread.Id, [new TextContent("hello")]));
        DeleteSessionRow(thread.Id);

        var secondChatClient = new RecordingChatClient("second answer");
        await using var secondFactory = CreateAgentFactory(secondChatClient);
        var secondService = CreateService(secondFactory, secondChatClient);
        await secondService.ResumeThreadAsync(thread.Id);

        await DrainAsync(secondService.SubmitInputAsync(thread.Id, [new TextContent("follow up")]));

        Assert.Equal(
            ["user:hello", "assistant:first answer", "user:follow up"],
            secondChatClient.LastMessages.Select(FormatMessage).ToList());
    }

    [Fact]
    public async Task QueuedInputOperations_AreSerializedPerThread()
    {
        IChatClient chatClient = new FakeChatClient([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);
        await using var agentFactory = CreateAgentFactory(chatClient);
        var svc = CreateService(agentFactory, chatClient);
        var thread = await svc.CreateThreadAsync(MakeIdentity());
        thread.Turns.Add(new SessionTurn
        {
            Id = "turn_001",
            ThreadId = thread.Id,
            Status = TurnStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        });

        var queuedInputs = new List<QueuedTurnInput>();
        for (var i = 0; i < 40; i++)
        {
            var text = $"queued {i}";
            queuedInputs.Add(await svc.EnqueueTurnInputAsync(
                thread.Id,
                [new TextContent(text)],
                inputSnapshot: new SessionInputSnapshot
                {
                    NativeInputParts = [new SessionWireInputPart { Type = "text", Text = text }],
                    MaterializedInputParts = [new SessionWireInputPart { Type = "text", Text = text }],
                    DisplayText = text
                }));
        }

        var operations = queuedInputs.Select(async (queued, index) =>
        {
            if (index % 2 == 0)
                await svc.RemoveQueuedTurnInputAsync(thread.Id, queued.Id);
            else
                await svc.SteerTurnAsync(thread.Id, "turn_001", queued.Id);
        }).ToArray();

        await Task.WhenAll(operations);

        var reloaded = await new ThreadStore(_tempDir).LoadThreadAsync(thread.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(20, reloaded.QueuedInputs.Count);
        Assert.Equal(20, reloaded.QueuedInputs.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(reloaded.QueuedInputs, queued => Assert.Equal("guidancePending", queued.Status));
        Assert.Equal(
            queuedInputs.Where((_, index) => index % 2 == 1).Select(q => q.Id).ToArray(),
            reloaded.QueuedInputs.Select(q => q.Id).ToArray());
    }

    private SessionService CreateService(
        AgentFactory agentFactory,
        IChatClient chatClient,
        TokenUsageStore? tokenUsageStore = null)
    {
        var defaultAgent = chatClient.AsAIAgent(new ChatClientAgentOptions());
        return new SessionService(
            agentFactory,
            defaultAgent,
            new SessionPersistenceService(new ThreadStore(_tempDir)),
            new SessionGate(),
            tokenUsageStore: tokenUsageStore);
    }

    private AgentFactory CreateAgentFactory(
        IChatClient chatClientFactory,
        IReadOnlyList<IAgentToolProvider>? toolProviders = null,
        Action<AppConfig>? configureConfig = null,
        IMemoryConsolidator? memoryConsolidator = null)
    {
        var config = new AppConfig
        {
            ApiKey = "sk-test-not-used-for-network",
            EndPoint = "https://127.0.0.1:9/v1"
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
            toolProviders: toolProviders ?? Array.Empty<IAgentToolProvider>(),
            memoryConsolidator: memoryConsolidator);
    }

    private SessionIdentity MakeIdentity() => new()
    {
        ChannelName = "test",
        UserId = "u",
        WorkspacePath = _tempDir
    };

    private static async Task DrainAsync(IAsyncEnumerable<SessionEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private static async Task<List<SessionEvent>> CollectAsync(IAsyncEnumerable<SessionEvent> events)
    {
        var collected = new List<SessionEvent>();
        await foreach (var evt in events)
            collected.Add(evt);
        return collected;
    }

    private static string FormatMessage(ChatMessage message)
    {
        var text = string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));
        var runtimeContextIndex = text.IndexOf("\n[Runtime Context]", StringComparison.Ordinal);
        if (runtimeContextIndex >= 0)
            text = text[..runtimeContextIndex];
        text = StripSystemReminderBlocks(text);
        return $"{message.Role}:{text.Trim()}";
    }

    private static string StripSystemReminderBlocks(string input)
    {
        const string openTag = "<system-reminder>";
        const string closeTag = "</system-reminder>";
        var text = input;
        while (true)
        {
            var open = text.IndexOf(openTag, StringComparison.Ordinal);
            if (open < 0)
                return text.TrimEnd();

            var close = text.IndexOf(closeTag, open + openTag.Length, StringComparison.Ordinal);
            if (close < 0)
                return text[..open].TrimEnd();

            text = (text[..open] + text[(close + closeTag.Length)..]).TrimEnd();
        }
    }

    private bool ThreadRowExists(string threadId)
    {
        using var connection = OpenStateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM threads WHERE thread_id = $thread_id LIMIT 1";
        command.Parameters.AddWithValue("$thread_id", threadId);
        return command.ExecuteScalar() != null;
    }

    private void DeleteSessionRow(string threadId)
    {
        using var connection = OpenStateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM thread_sessions WHERE thread_id = $thread_id";
        command.Parameters.AddWithValue("$thread_id", threadId);
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenStateConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_tempDir, "state.db"),
                Mode = SqliteOpenMode.ReadWrite
            }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class FakeChatClient(ChatResponseUpdate[] streamUpdates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, [new TextContent("ok")])]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in streamUpdates)
                yield return update;
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw exception;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Environment.TickCount < 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(string.Empty)]);
            await Task.Yield();
            throw exception;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class BlockingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, [new TextContent("ok")])]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingChatClient(string responseText) : IChatClient
    {
        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = chatMessages.ToList();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, [new TextContent(responseText)])]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = chatMessages.ToList();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(responseText)]);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingToolProvider : IAgentToolProvider
    {
        public int Priority => 10;

        public List<ToolProviderContext> Contexts { get; } = [];

        public IEnumerable<AITool> CreateTools(ToolProviderContext context)
        {
            Contexts.Add(context);
            return [];
        }
    }

    private sealed class CapturingForkConsolidator : IMemoryForkConsolidator
    {
        private readonly TaskCompletionSource<PromptRequestSnapshot?> _snapshotSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MemoryConsolidationResult> ConsolidateAsync(
            IReadOnlyList<ChatMessage> messagesToArchive,
            CancellationToken cancellationToken = default)
        {
            _snapshotSource.TrySetResult(null);
            return Task.FromResult(MemoryConsolidationResult.Skipped("legacy_path"));
        }

        public Task<MemoryConsolidationResult> ConsolidateAsync(
            IReadOnlyList<ChatMessage> messagesToArchive,
            PromptRequestSnapshot? snapshot,
            CancellationToken cancellationToken = default)
        {
            _snapshotSource.TrySetResult(snapshot);
            return Task.FromResult(MemoryConsolidationResult.Skipped("captured"));
        }

        public async Task<PromptRequestSnapshot?> WaitForSnapshotAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var registration = cts.Token.Register(
                static state => ((TaskCompletionSource<PromptRequestSnapshot?>)state!).TrySetCanceled(),
                _snapshotSource);
            return await _snapshotSource.Task;
        }
    }
}
