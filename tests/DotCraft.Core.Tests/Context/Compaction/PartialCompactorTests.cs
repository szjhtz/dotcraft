using DotCraft.Context;
using DotCraft.Context.Compaction;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Context.Compaction;

public sealed class PartialCompactorTests
{
    [Fact]
    public void CalculateSplitIndex_SmallConversationKeepsEverything()
    {
        var cfg = new CompactionConfig
        {
            KeepRecentMinTokens = 100,
            KeepRecentMinGroups = 5,
            KeepRecentMaxTokens = 200,
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant, "hello"),
        };

        Assert.Equal(0, PartialCompactor.CalculateSplitIndex(messages, cfg));
    }

    [Fact]
    public void CalculateSplitIndex_LargeConversationSplitsPreservingTail()
    {
        var cfg = new CompactionConfig
        {
            KeepRecentMinTokens = 1,
            KeepRecentMinGroups = 2,
            KeepRecentMaxTokens = 100_000,
        };

        var messages = new List<ChatMessage>();
        for (var round = 0; round < 5; round++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"user turn {round}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"assistant turn {round}"));
        }

        var splitIndex = PartialCompactor.CalculateSplitIndex(messages, cfg);
        // KeepRecentMinGroups = 2 → preserve 2 groups of 2 messages each → split after 6 messages.
        Assert.Equal(6, splitIndex);
    }

    [Fact]
    public async Task CompactAsync_EmptyHistoryReturnsReason()
    {
        var cfg = new CompactionConfig();
        var partial = new PartialCompactor(new StubChatClient("summary"), cfg);

        var result = await partial.CompactAsync(Array.Empty<ChatMessage>());
        Assert.Null(result.Result);
        Assert.Equal("empty_history", result.Reason);
    }

    [Fact]
    public async Task CompactAsync_SummarizesPrefixAndRetainsTail()
    {
        var cfg = new CompactionConfig
        {
            KeepRecentMinTokens = 1,
            KeepRecentMinGroups = 1,
            KeepRecentMaxTokens = 100_000,
        };
        var client = new StubChatClient("<analysis>thinking</analysis><summary>important bits</summary>");
        var partial = new PartialCompactor(client, cfg);

        var messages = new List<ChatMessage>();
        for (var round = 0; round < 4; round++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"user turn {round}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"assistant turn {round}"));
        }

        var result = await partial.CompactAsync(messages);
        Assert.NotNull(result.Result);
        Assert.True(result.Result!.SummarizedPrefix.Count > 0);
        Assert.True(result.Result.PreservedTail.Count > 0);
        Assert.Contains("important bits", result.Result.FormattedSummary);
        // analysis should be stripped from FormattedSummary.
        Assert.DoesNotContain("<analysis>", result.Result.FormattedSummary);
        Assert.Equal("<analysis>thinking</analysis><summary>important bits</summary>", result.Result.RawSummary);
    }

    [Fact]
    public async Task CompactAsync_WithSnapshotRunsMaintenanceFork()
    {
        var cfg = new CompactionConfig
        {
            KeepRecentMinTokens = 1,
            KeepRecentMinGroups = 1,
            KeepRecentMaxTokens = 100_000,
        };
        var client = new StubChatClient("<analysis>thinking</analysis><summary>important bits</summary>");
        var tool = AIFunctionFactory.Create(() => "ok", name: "ReadFile", description: "Read a file.");
        var partial = new PartialCompactor(client, cfg, new MaintenanceForkRunner(client));

        var messages = new List<ChatMessage>();
        for (var round = 0; round < 4; round++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"user turn {round}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"assistant turn {round}"));
        }
        var snapshot = PromptRequestSnapshot.Capture(
            messages,
            new ChatOptions
            {
                Instructions = "stable base",
                ModelId = "gpt-test",
                Tools = [tool]
            });

        var result = await partial.CompactAsync(messages, snapshot);

        Assert.NotNull(result.Result);
        Assert.Contains("important bits", result.Result!.FormattedSummary);
        Assert.Equal(["user:user turn 0", "assistant:assistant turn 0"], client.Messages.Take(2).Select(m => $"{m.Role}:{m.Text}"));
        Assert.Equal(ChatRole.User, client.Messages[^1].Role);
        Assert.Contains("## Maintenance Task", client.Messages[^1].Text);
        Assert.Contains("Task: context_compaction", client.Messages[^1].Text);
        Assert.Equal("stable base", client.Options?.Instructions);
        Assert.Equal("gpt-test", client.Options?.ModelId);
        var capturedTool = Assert.Single(client.Options?.Tools ?? []);
        Assert.Equal("ReadFile", capturedTool.Name);
    }

    [Fact]
    public async Task CompactAsync_ReturnsReasonOnChatClientFailure()
    {
        var cfg = new CompactionConfig
        {
            KeepRecentMinTokens = 1,
            KeepRecentMinGroups = 1,
        };
        var partial = new PartialCompactor(new StubChatClient(string.Empty, throwOnCall: true), cfg);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "u1"),
            new(ChatRole.Assistant, "a1"),
            new(ChatRole.User, "u2"),
            new(ChatRole.Assistant, "a2"),
        };

        var result = await partial.CompactAsync(messages);
        Assert.Null(result.Result);
        Assert.Equal("summary_unavailable", result.Reason);
    }

    [Fact]
    public async Task CompactAsync_RetriesPromptTooLongByDroppingOldestGroups()
    {
        var cfg = new CompactionConfig
        {
            KeepRecentMinTokens = 1,
            KeepRecentMinGroups = 1,
            KeepRecentMaxTokens = 100_000,
        };
        var client = new StubChatClient(
            "<analysis>thinking</analysis><summary>retried summary</summary>",
            promptTooLongFailures: 1);
        var partial = new PartialCompactor(client, cfg);

        var messages = new List<ChatMessage>();
        for (var round = 0; round < 5; round++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"user turn {round}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"assistant turn {round}"));
        }

        var result = await partial.CompactAsync(messages);

        Assert.NotNull(result.Result);
        Assert.Equal(2, client.CallCount);
        Assert.Contains("retried summary", result.Result!.FormattedSummary);
        Assert.DoesNotContain(client.Messages, m => m.Text?.Contains("user turn 0", StringComparison.Ordinal) == true);
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly string _responseText;
        private readonly bool _throwOnCall;
        private int _promptTooLongFailures;

        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];
        public ChatOptions? Options { get; private set; }
        public int CallCount { get; private set; }

        public StubChatClient(string responseText, bool throwOnCall = false, int promptTooLongFailures = 0)
        {
            _responseText = responseText;
            _throwOnCall = throwOnCall;
            _promptTooLongFailures = promptTooLongFailures;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_throwOnCall)
                throw new InvalidOperationException("boom");
            if (_promptTooLongFailures > 0)
            {
                _promptTooLongFailures--;
                throw new InvalidOperationException("prompt_too_long");
            }

            Messages = messages.ToArray();
            Options = options;
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseText));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
