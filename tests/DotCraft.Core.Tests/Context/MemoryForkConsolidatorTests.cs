using DotCraft.Context;
using DotCraft.Memory;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Context;

public sealed class MemoryForkConsolidatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MemoryFork_" + Guid.NewGuid().ToString("N")[..8]);

    public MemoryForkConsolidatorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    [Fact]
    public async Task ConsolidateAsync_WithSameModelSnapshotRunsForkAndSavesStructuredResult()
    {
        var chatClient = new RecordingChatClient("""
        {
          "history_entry": "[2026-05-06 10:00] User prefers blue.",
          "memory_update": "- User prefers blue."
        }
        """);
        var memoryStore = new MemoryStore(_tempDir);
        var legacy = new FakeMemoryConsolidator(MemoryConsolidationResult.Failed("legacy should not run"));
        var consolidator = new MemoryForkConsolidator(
            new MaintenanceForkRunner(chatClient),
            legacy,
            memoryStore,
            mainModelId: "gpt-test",
            consolidationModelId: "gpt-test");
        var tool = AIFunctionFactory.Create(() => "ok", name: "ReadFile", description: "Read a file.");
        var snapshot = PromptRequestSnapshot.Capture(
            [new ChatMessage(ChatRole.User, "remember blue")],
            new ChatOptions
            {
                Instructions = "stable base",
                ModelId = "gpt-test",
                Tools = [tool]
            });

        var result = await consolidator.ConsolidateAsync(
            [new ChatMessage(ChatRole.User, "remember blue")],
            snapshot);

        Assert.Equal(MemoryConsolidationOutcome.Succeeded, result.Outcome);
        Assert.True(result.MemoryWritten);
        Assert.True(result.HistoryWritten);
        Assert.Contains("User prefers blue", memoryStore.ReadLongTerm());
        Assert.Contains("User prefers blue", memoryStore.ReadHistory());
        Assert.Equal(0, legacy.Calls);
        Assert.Equal("stable base", chatClient.Options?.Instructions);
        Assert.Equal("gpt-test", chatClient.Options?.ModelId);
        Assert.Equal("ReadFile", Assert.Single(chatClient.Options?.Tools ?? []).Name);
        Assert.Contains("## Maintenance Task", chatClient.Messages[^1].Text);
        Assert.Contains("Task: memory_consolidation", chatClient.Messages[^1].Text);
    }

    [Fact]
    public async Task ConsolidateAsync_WithDifferentModelFallsBackToLegacy()
    {
        var chatClient = new RecordingChatClient("{}");
        var memoryStore = new MemoryStore(_tempDir);
        var legacy = new FakeMemoryConsolidator(MemoryConsolidationResult.Skipped("legacy_fallback"));
        var consolidator = new MemoryForkConsolidator(
            new MaintenanceForkRunner(chatClient),
            legacy,
            memoryStore,
            mainModelId: "gpt-main",
            consolidationModelId: "gpt-small");
        var snapshot = PromptRequestSnapshot.Capture(
            [new ChatMessage(ChatRole.User, "remember blue")],
            new ChatOptions { ModelId = "gpt-main" });

        var result = await consolidator.ConsolidateAsync(
            [new ChatMessage(ChatRole.User, "remember blue")],
            snapshot);

        Assert.Equal(MemoryConsolidationOutcome.Skipped, result.Outcome);
        Assert.Equal("legacy_fallback", result.Message);
        Assert.Equal(1, legacy.Calls);
        Assert.Empty(chatClient.Messages);
    }

    [Fact]
    public async Task ConsolidateAsync_WithInvalidJsonFallsBackToLegacy()
    {
        var chatClient = new RecordingChatClient("not json");
        var memoryStore = new MemoryStore(_tempDir);
        var legacy = new FakeMemoryConsolidator(MemoryConsolidationResult.Skipped("legacy_fallback"));
        var consolidator = new MemoryForkConsolidator(
            new MaintenanceForkRunner(chatClient),
            legacy,
            memoryStore,
            mainModelId: "gpt-test",
            consolidationModelId: "gpt-test");
        var snapshot = PromptRequestSnapshot.Capture(
            [new ChatMessage(ChatRole.User, "remember blue")],
            new ChatOptions { ModelId = "gpt-test" });

        var result = await consolidator.ConsolidateAsync(
            [new ChatMessage(ChatRole.User, "remember blue")],
            snapshot);

        Assert.Equal(MemoryConsolidationOutcome.Skipped, result.Outcome);
        Assert.Equal("legacy_fallback", result.Message);
        Assert.Equal(1, legacy.Calls);
        Assert.NotEmpty(chatClient.Messages);
    }

    private sealed class FakeMemoryConsolidator(MemoryConsolidationResult result) : IMemoryConsolidator
    {
        public int Calls { get; private set; }

        public Task<MemoryConsolidationResult> ConsolidateAsync(
            IReadOnlyList<ChatMessage> messagesToArchive,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingChatClient(string responseText) : IChatClient
    {
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages = messages.ToArray();
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
