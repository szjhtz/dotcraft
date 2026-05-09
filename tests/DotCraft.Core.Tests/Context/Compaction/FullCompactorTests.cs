using DotCraft.Context;
using DotCraft.Context.Compaction;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Context.Compaction;

public sealed class FullCompactorTests
{
    [Fact]
    public async Task CompactAsync_WithCompatibleSnapshot_PreservesCachedPrefixAndAppendsTailBeforeTask()
    {
        var client = new RecordingChatClient("<analysis>ok</analysis><summary>cached full summary</summary>");
        var full = new FullCompactor(client, new MaintenanceForkRunner(client));
        var tool = AIFunctionFactory.Create(() => "ok", name: "ReadFile", description: "Read a file.");
        var snapshotMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "first user"),
            new(ChatRole.Assistant, "first assistant"),
            new(ChatRole.User, "second user")
        };
        var snapshot = PromptRequestSnapshot.Capture(
            snapshotMessages,
            new ChatOptions
            {
                Instructions = "stable base instructions",
                ModelId = "gpt-test",
                Tools = [tool]
            });
        var history = snapshotMessages
            .Concat([new ChatMessage(ChatRole.Assistant, "second assistant")])
            .ToList();

        var result = await full.CompactAsync(history, snapshot);

        Assert.NotNull(result.Result);
        Assert.Contains("cached full summary", result.Result!.FormattedSummary);
        Assert.Equal(
            ["user:first user", "assistant:first assistant", "user:second user"],
            client.Messages.Take(3).Select(message => $"{message.Role}:{message.Text}"));
        Assert.Equal("assistant:second assistant", $"{client.Messages[3].Role}:{client.Messages[3].Text}");
        Assert.Equal(ChatRole.User, client.Messages[^1].Role);
        Assert.Contains("Task: context_compaction", client.Messages[^1].Text);
        Assert.Equal("stable base instructions", client.Options?.Instructions);
        Assert.Equal("gpt-test", client.Options?.ModelId);
        var capturedTool = Assert.Single(client.Options?.Tools ?? []);
        Assert.Equal("ReadFile", capturedTool.Name);
    }

    [Fact]
    public async Task CompactAsync_LegacyPathUsesFullContextBoundaryInstructionsAndNoTools()
    {
        var client = new RecordingChatClient("<analysis>ok</analysis><summary>legacy full summary</summary>");
        var full = new FullCompactor(client);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "first user"),
            new(ChatRole.Assistant, "first assistant")
        };

        var result = await full.CompactAsync(history, snapshot: null);

        Assert.NotNull(result.Result);
        Assert.Contains("legacy full summary", result.Result!.FormattedSummary);
        Assert.Equal(ChatRole.System, client.Messages[0].Role);
        Assert.Contains("Summarize the complete conversation visible above", client.Messages[0].Text);
        Assert.Contains("replace the current model-visible history", client.Messages[0].Text);
        Assert.Null(client.Options?.Tools);
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

        public void Dispose() { }
    }
}
