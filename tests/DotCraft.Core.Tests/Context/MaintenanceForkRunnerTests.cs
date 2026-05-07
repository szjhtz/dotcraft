using DotCraft.Context;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Context;

public sealed class MaintenanceForkRunnerTests
{
    [Fact]
    public async Task RunAsync_ReusesSnapshotPrefixAndAppendsMaintenanceTask()
    {
        var chatClient = new RecordingChatClient("<summary>important bits</summary>");
        var runner = new MaintenanceForkRunner(chatClient);
        var tool = AIFunctionFactory.Create(() => "ok", name: "ReadFile", description: "Read a file.");
        var snapshot = PromptRequestSnapshot.Capture(
            [
                new ChatMessage(ChatRole.User, "start"),
                new ChatMessage(ChatRole.Assistant, "working")
            ],
            new ChatOptions
            {
                Instructions = "stable base",
                ModelId = "gpt-test",
                Tools = [tool],
                AllowMultipleToolCalls = true
            },
            mode: "agent",
            threadId: "thread_1",
            turnId: "turn_1");

        var result = await runner.RunAsync(
            snapshot,
            new MaintenanceForkTask(MaintenanceForkTaskKind.ContextCompaction, "Summarize older context."));

        Assert.Null(result.FallbackReason);
        Assert.Equal("<summary>important bits</summary>", result.Text);
        Assert.Equal(["user:start", "assistant:working"], chatClient.Messages.Take(2).Select(m => $"{m.Role}:{m.Text}"));
        Assert.Equal(3, chatClient.Messages.Count);
        Assert.Equal(ChatRole.User, chatClient.Messages[^1].Role);
        Assert.Contains("<system-reminder>", chatClient.Messages[^1].Text);
        Assert.Contains("## Maintenance Task", chatClient.Messages[^1].Text);
        Assert.Contains("Task: context_compaction", chatClient.Messages[^1].Text);
        Assert.DoesNotContain("<dotcraft_maintenance_task>", chatClient.Messages[^1].Text);
        Assert.Contains("Summarize older context.", chatClient.Messages[^1].Text);
        Assert.Equal("stable base", chatClient.Options?.Instructions);
        Assert.Equal("gpt-test", chatClient.Options?.ModelId);
        Assert.True(chatClient.Options?.AllowMultipleToolCalls);
        var capturedTool = Assert.Single(chatClient.Options?.Tools ?? []);
        Assert.Equal("ReadFile", capturedTool.Name);
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
