using DotCraft.Context;
using DotCraft.Tracing;
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

    [Fact]
    public async Task RunAsync_RecordsMaintenanceRequestAndTextResponse()
    {
        var store = new TraceStore();
        var collector = new TraceCollector(store);
        var chatClient = new RecordingChatClient("<summary>important bits</summary>");
        var runner = new MaintenanceForkRunner(chatClient, collector);
        var tool = AIFunctionFactory.Create(() => "ok", name: "ReadFile", description: "Read a file.");
        var snapshot = PromptRequestSnapshot.Capture(
            [new ChatMessage(ChatRole.User, "start")],
            new ChatOptions
            {
                Instructions = "stable base",
                ModelId = "gpt-test",
                Tools = [tool]
            },
            providerId: "provider-test",
            mode: "agent",
            threadId: "thread_1",
            turnId: "turn_1");

        var result = await runner.RunAsync(
            snapshot,
            new MaintenanceForkTask(MaintenanceForkTaskKind.ContextCompaction, "Summarize older context."));

        Assert.Null(result.FallbackReason);
        var events = store.GetEvents("thread_1");
        var request = Assert.Single(events, e => e.Type == TraceEventType.MaintenanceForkRequest);
        var response = Assert.Single(events, e => e.Type == TraceEventType.MaintenanceForkResponse);
        Assert.Contains("Summarize older context.", request.Content);
        Assert.Equal("<summary>important bits</summary>", response.Content);
        Assert.Contains("\"providerId\":\"provider-test\"", request.MetadataJson);
        Assert.Contains("\"toolCount\":1", request.MetadataJson);
        Assert.Contains("\"fallbackReason\":null", response.MetadataJson);
    }

    [Fact]
    public async Task RunAsync_RecordsEmptyResponseFallback()
    {
        var store = new TraceStore();
        var collector = new TraceCollector(store);
        var runner = new MaintenanceForkRunner(new RecordingChatClient(""), collector);
        var snapshot = CreateSnapshot();

        var result = await runner.RunAsync(
            snapshot,
            new MaintenanceForkTask(MaintenanceForkTaskKind.ContextCompaction, "Summarize older context."));

        Assert.Equal("empty_response", result.FallbackReason);
        var response = Assert.Single(
            store.GetEvents("thread_1"),
            e => e.Type == TraceEventType.MaintenanceForkResponse);
        Assert.Equal("(empty)", response.Content);
        Assert.Contains("\"fallbackReason\":\"empty_response\"", response.MetadataJson);
    }

    [Fact]
    public async Task RunAsync_RecordsToolCallOnlyResponseMetadata()
    {
        var store = new TraceStore();
        var collector = new TraceCollector(store);
        var toolCallMessage = new ChatMessage(
            ChatRole.Assistant,
            (IList<AIContent>)
            [
                new FunctionCallContent(
                    "call_1",
                    "ReadFile",
                    new Dictionary<string, object?> { ["path"] = "README.md" })
            ]);
        var runner = new MaintenanceForkRunner(
            new RecordingChatClient(new ChatResponse(toolCallMessage)),
            collector);
        var snapshot = CreateSnapshot();

        var result = await runner.RunAsync(
            snapshot,
            new MaintenanceForkTask(MaintenanceForkTaskKind.ContextCompaction, "Summarize older context."));

        Assert.Equal("tool_call_without_text", result.FallbackReason);
        var response = Assert.Single(
            store.GetEvents("thread_1"),
            e => e.Type == TraceEventType.MaintenanceForkResponse);
        Assert.Contains("\"fallbackReason\":\"tool_call_without_text\"", response.MetadataJson);
        Assert.Contains("\"type\":\"function_call\"", response.MetadataJson);
        Assert.Contains("\"name\":\"ReadFile\"", response.MetadataJson);
        Assert.Contains("\"callId\":\"call_1\"", response.MetadataJson);
    }

    [Fact]
    public async Task RunAsync_RecordsExceptionFallback()
    {
        var store = new TraceStore();
        var collector = new TraceCollector(store);
        var runner = new MaintenanceForkRunner(
            new RecordingChatClient(new InvalidOperationException("provider failed")),
            collector);
        var snapshot = CreateSnapshot();

        var result = await runner.RunAsync(
            snapshot,
            new MaintenanceForkTask(MaintenanceForkTaskKind.ContextCompaction, "Summarize older context."));

        Assert.Equal("provider failed", result.FallbackReason);
        var response = Assert.Single(
            store.GetEvents("thread_1"),
            e => e.Type == TraceEventType.MaintenanceForkResponse);
        Assert.Equal("(empty)", response.Content);
        Assert.Contains("\"fallbackReason\":\"provider failed\"", response.MetadataJson);
    }

    private static PromptRequestSnapshot CreateSnapshot() =>
        PromptRequestSnapshot.Capture(
            [new ChatMessage(ChatRole.User, "start")],
            new ChatOptions { Instructions = "stable base", ModelId = "gpt-test" },
            mode: "agent",
            threadId: "thread_1",
            turnId: "turn_1");

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ChatResponse? _response;
        private readonly Exception? _exception;

        public RecordingChatClient(string responseText)
            : this(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)))
        {
        }

        public RecordingChatClient(ChatResponse response)
        {
            _response = response;
        }

        public RecordingChatClient(Exception exception)
        {
            _exception = exception;
        }

        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages = messages.ToArray();
            Options = options;
            if (_exception is not null)
                throw _exception;

            return Task.FromResult(_response!);
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
