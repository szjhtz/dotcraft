using System.Text.Json;
using DotCraft.Agents;
using DotCraft.Configuration;
using DotCraft.Tracing;
using Microsoft.Extensions.AI;
using System.ClientModel.Primitives;

namespace DotCraft.Tests.Agents;

public sealed class PromptCachingChatClientTests
{
    [Fact]
    public void Prepare_ForClaudeModel_MarksLatestUserMessageInsteadOfSystem()
    {
        var client = CreateClient("anthropic/claude-opus-4-1");
        var system = new ChatMessage(ChatRole.System, "stable system prompt");
        var user = new ChatMessage(ChatRole.User, "hello");

        var prepared = client.Prepare([system, user], null);

        var systemText = Assert.IsType<TextContent>(Assert.Single(prepared.Messages[0].Contents));
        Assert.Null(systemText.AdditionalProperties);
        var userText = AssertLastTextContent(prepared.Messages[1]);
        AssertCacheControl(userText, expectedTtl: null);
    }

    [Fact]
    public void Prepare_WithInstructions_ConvertsInstructionsButMarksLatestUser()
    {
        var client = CreateClient("claude-3-5-sonnet");
        var options = new ChatOptions { Instructions = "stable system prompt" };
        var user = new ChatMessage(ChatRole.User, "hello");

        var prepared = client.Prepare([user], options);

        Assert.Null(prepared.Options!.Instructions);
        var system = Assert.Single(prepared.Messages, m => m.Role == ChatRole.System);
        var systemText = Assert.IsType<TextContent>(Assert.Single(system.Contents));
        Assert.Equal("stable system prompt", systemText.Text);
        Assert.Null(systemText.AdditionalProperties);

        var userText = AssertLastTextContent(prepared.Messages.Last());
        AssertCacheControl(userText, expectedTtl: null);
        Assert.Equal("stable system prompt", options.Instructions);
    }

    [Fact]
    public void Prepare_WithToolTail_MarksLatestUserAssistantAndToolResult()
    {
        var client = CreateClient("claude-opus-4-1");
        var firstResult = new FunctionResultContent("call_1", "first");
        var secondResult = new FunctionResultContent("call_2", "second")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["existing"] = true
            },
            Exception = new InvalidOperationException("boom")
        };
        var tool = new ChatMessage(ChatRole.Tool, (IList<AIContent>)[firstResult, secondResult]);

        var prepared = client.Prepare([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "assistant response"),
            tool
        ], null);

        var preparedUserText = AssertLastTextContent(prepared.Messages[0]);
        AssertCacheControl(preparedUserText, expectedTtl: null);
        var preparedAssistantText = AssertLastTextContent(prepared.Messages[1]);
        AssertCacheControl(preparedAssistantText, expectedTtl: null);
        Assert.Equal(4, prepared.Messages.Count);
        var firstPrepared = Assert.IsType<FunctionResultContent>(Assert.Single(prepared.Messages[2].Contents));
        var secondPrepared = Assert.IsType<FunctionResultContent>(Assert.Single(prepared.Messages[3].Contents));
        Assert.Same(firstResult, firstPrepared);
        Assert.NotSame(secondResult, secondPrepared);
        Assert.Equal(ChatRole.Tool, prepared.Messages[2].Role);
        Assert.Equal(ChatRole.Tool, prepared.Messages[3].Role);
        Assert.Equal("call_2", secondPrepared.CallId);
        Assert.Equal("second", secondPrepared.Result);
        Assert.Same(secondResult.Exception, secondPrepared.Exception);
        Assert.True((bool)secondPrepared.AdditionalProperties!["existing"]!);
        AssertCacheControl(secondPrepared, expectedTtl: null);
        Assert.False(secondResult.AdditionalProperties?.ContainsKey(PromptCachingChatClient.CacheControlKey) ?? false);
    }

    [Fact]
    public void Prepare_WithToolTailAndNoAssistantText_MarksLatestUserAndToolResult()
    {
        var client = CreateClient("claude-opus-4-1");
        var tool = new ChatMessage(ChatRole.Tool, (IList<AIContent>)[
            new FunctionResultContent("call_1", "result text")
        ]);

        var prepared = client.Prepare([new ChatMessage(ChatRole.User, "hello"), tool], null);

        var preparedUserText = AssertLastTextContent(prepared.Messages[0]);
        AssertCacheControl(preparedUserText, expectedTtl: null);
        Assert.NotSame(tool, prepared.Messages[1]);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(prepared.Messages[1].Contents));
        AssertCacheControl(result, expectedTtl: null);
        Assert.Equal("call_1", result.CallId);
        Assert.Equal("result text", result.Result);
    }

    [Fact]
    public void Prepare_WithTextContentToolResult_MarksToolResultWithoutMutatingOriginal()
    {
        var client = CreateClient("claude-opus-4-1");
        var toolResultContents = (IList<AIContent>)[new TextContent("file contents")];
        var originalResult = new FunctionResultContent("call_1", toolResultContents);
        var tool = new ChatMessage(ChatRole.Tool, (IList<AIContent>)[originalResult]);

        var prepared = client.Prepare([
            new ChatMessage(ChatRole.User, "hello"),
            tool
        ], null);

        Assert.NotSame(tool, prepared.Messages[1]);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(prepared.Messages[1].Contents));
        Assert.NotSame(originalResult, result);
        Assert.Equal("call_1", result.CallId);
        Assert.Same(toolResultContents, result.Result);
        AssertCacheControl(result, expectedTtl: null);
        Assert.Null(originalResult.AdditionalProperties);
    }

    [Fact]
    public void Prepare_WithMixedToolResult_DoesNotMarkToolResult()
    {
        var client = CreateClient("claude-opus-4-1");
        var originalResult = new FunctionResultContent(
            "call_1",
            (IList<AIContent>)[
                new TextContent("text"),
                new DataContent(new BinaryData([1, 2, 3]), "image/png")
            ]);
        var tool = new ChatMessage(ChatRole.Tool, (IList<AIContent>)[originalResult]);

        var prepared = client.Prepare([
            new ChatMessage(ChatRole.User, "hello"),
            tool
        ], null);

        Assert.Same(tool, prepared.Messages[1]);
        Assert.Null(originalResult.AdditionalProperties);
    }

    [Fact]
    public void Prepare_ForNonMatchingModel_LeavesMessagesAndOptionsUnchanged()
    {
        var client = CreateClient("gpt-4o-mini");
        var options = new ChatOptions { Instructions = "stable system prompt" };
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };

        var prepared = client.Prepare(messages, options);

        Assert.Same(messages, prepared.Messages);
        Assert.Same(options, prepared.Options);
    }

    [Fact]
    public void Prepare_WhenDisabled_LeavesMessagesAndOptionsUnchanged()
    {
        var config = new AppConfig.PromptCachingConfig { Enabled = false };
        var client = new PromptCachingChatClient(new CaptureChatClient(), config, "claude-opus-4-1");
        var options = new ChatOptions { Instructions = "stable system prompt" };
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };

        var prepared = client.Prepare(messages, options);

        Assert.Same(messages, prepared.Messages);
        Assert.Same(options, prepared.Options);
    }

    [Fact]
    public void Prepare_WithTtl_AddsTtlToCacheControl()
    {
        var client = CreateClient("claude-opus-4-1", ttl: "1h");

        var prepared = client.Prepare([new ChatMessage(ChatRole.User, "hello")], null);

        var user = Assert.Single(prepared.Messages);
        var text = AssertLastTextContent(user);
        AssertCacheControl(text, expectedTtl: "1h");
    }

    [Fact]
    public void Prepare_DoesNotMutateOriginalMessagesOrContents()
    {
        var client = CreateClient("claude-opus-4-1");
        var text = new TextContent("hello");
        var user = new ChatMessage(ChatRole.User, (IList<AIContent>)[text]);

        var prepared = client.Prepare([user], null);

        Assert.NotSame(user, prepared.Messages[0]);
        Assert.Same(text, user.Contents[0]);
        Assert.Null(text.AdditionalProperties);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_UsesSamePreparationLogic()
    {
        var capture = new CaptureChatClient();
        var client = new PromptCachingChatClient(capture, new AppConfig.PromptCachingConfig(), "claude-opus-4-1");

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
        }

        var text = AssertLastTextContent(capture.LastMessages![0]);
        AssertCacheControl(text, expectedTtl: null);
    }

    [Fact]
    public void OpenAIAdapter_SingleTextMessageAddsRootMarkerForPipelineRewrite()
    {
        var client = CreateClient("claude-opus-4-1");
        var prepared = client.Prepare([new ChatMessage(ChatRole.User, "hello")], null);

        var openAiMessage = OpenAI.Chat.MicrosoftExtensionsAIChatExtensions
            .AsOpenAIChatMessages(prepared.Messages, prepared.Options)
            .Single();
        var json = ModelReaderWriter.Write(openAiMessage).ToString();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("hello", root.GetProperty("content").GetString());
        Assert.Equal("ephemeral", root.GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());

        var rewritten = PromptCacheControlPipelinePolicy.RewriteJson(
            $$"""{"messages":[{{json}}]}""");
        Assert.NotNull(rewritten);
        using var rewrittenDocument = JsonDocument.Parse(rewritten);
        var message = rewrittenDocument.RootElement.GetProperty("messages")[0];
        Assert.False(message.TryGetProperty(PromptCachingChatClient.CacheControlKey, out _));
        var content = message.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("hello", content[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", content[0].GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());
    }

    [Fact]
    public void OpenAIAdapter_ContentArrayMessagePreservesCacheControlOnTextBlock()
    {
        var client = CreateClient("claude-opus-4-1");
        var prepared = client.Prepare([
            new ChatMessage(ChatRole.User, (IList<AIContent>)[
                new TextContent("hello"),
                new TextContent("again")
            ])
        ], null);

        var openAiMessage = OpenAI.Chat.MicrosoftExtensionsAIChatExtensions
            .AsOpenAIChatMessages(prepared.Messages, prepared.Options)
            .Single();
        var json = ModelReaderWriter.Write(openAiMessage).ToString();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty(PromptCachingChatClient.CacheControlKey, out _));
        var content = root.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var block = content[content.GetArrayLength() - 1];
        Assert.Equal("ephemeral", block.GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());
    }

    [Fact]
    public void OpenAIAdapter_ToolResultMovesCacheControlToTextBlock()
    {
        var client = CreateClient("claude-opus-4-1");
        var prepared = client.Prepare([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, (IList<AIContent>)[
                new FunctionCallContent("call_1", "ReadFile", new Dictionary<string, object?>())
            ]),
            new ChatMessage(ChatRole.Tool, (IList<AIContent>)[
                new FunctionResultContent("call_1", "result text")
            ])
        ], null);

        var openAiMessage = OpenAI.Chat.MicrosoftExtensionsAIChatExtensions
            .AsOpenAIChatMessages(prepared.Messages, prepared.Options)
            .Last();
        var json = ModelReaderWriter.Write(openAiMessage).ToString();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("tool", root.GetProperty("role").GetString());
        Assert.Equal("call_1", root.GetProperty("tool_call_id").GetString());
        Assert.Equal("result text", root.GetProperty("content").GetString());
        Assert.Equal("ephemeral", root.GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());

        var rewritten = PromptCacheControlPipelinePolicy.RewriteJson(
            $$"""{"messages":[{{json}}]}""");
        Assert.NotNull(rewritten);
        using var rewrittenDocument = JsonDocument.Parse(rewritten);
        var message = rewrittenDocument.RootElement.GetProperty("messages")[0];
        Assert.Equal("tool", message.GetProperty("role").GetString());
        Assert.Equal("call_1", message.GetProperty("tool_call_id").GetString());
        Assert.False(message.TryGetProperty(PromptCachingChatClient.CacheControlKey, out _));
        var content = message.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("result text", content[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", content[0].GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());
    }

    [Fact]
    public void OpenAIAdapter_TextContentToolResultMovesCacheControlToTextBlock()
    {
        var client = CreateClient("claude-opus-4-1");
        var toolResultContents = (IList<AIContent>)[
            new TextContent("line one"),
            new TextContent("line two")
        ];
        var expectedWireText = JsonSerializer.Serialize(toolResultContents, AIJsonUtilities.DefaultOptions);
        ChatMessage[] messages = [
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, (IList<AIContent>)[
                new FunctionCallContent("call_1", "ReadFile", new Dictionary<string, object?>())
            ]),
            new ChatMessage(ChatRole.Tool, (IList<AIContent>)[
                new FunctionResultContent("call_1", toolResultContents)
            ])
        ];
        var unmarkedOpenAiMessage = OpenAI.Chat.MicrosoftExtensionsAIChatExtensions
            .AsOpenAIChatMessages(messages, null)
            .Last();
        var unmarkedJson = ModelReaderWriter.Write(unmarkedOpenAiMessage).ToString();
        using var unmarkedDocument = JsonDocument.Parse(unmarkedJson);

        var prepared = client.Prepare(messages, null);

        var openAiMessage = OpenAI.Chat.MicrosoftExtensionsAIChatExtensions
            .AsOpenAIChatMessages(prepared.Messages, prepared.Options)
            .Last();
        var json = ModelReaderWriter.Write(openAiMessage).ToString();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("tool", root.GetProperty("role").GetString());
        Assert.Equal("call_1", root.GetProperty("tool_call_id").GetString());
        Assert.Equal(
            unmarkedDocument.RootElement.GetProperty("content").GetString(),
            root.GetProperty("content").GetString());
        Assert.Equal(expectedWireText, root.GetProperty("content").GetString());
        Assert.Equal("ephemeral", root.GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());

        var rewritten = PromptCacheControlPipelinePolicy.RewriteJson(
            $$"""{"messages":[{{json}}]}""");
        Assert.NotNull(rewritten);
        using var rewrittenDocument = JsonDocument.Parse(rewritten);
        var message = rewrittenDocument.RootElement.GetProperty("messages")[0];
        Assert.False(message.TryGetProperty(PromptCachingChatClient.CacheControlKey, out _));
        var content = message.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(expectedWireText, content[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", content[0].GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());
    }

    [Fact]
    public async Task RollingBreakpoints_RestoresPreviousAssistantAndAddsNewAssistant()
    {
        var capture = new CaptureChatClient();
        var client = CreateClient("claude-opus-4-1", capture: capture);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello")
        ]);
        AssertCacheControl(AssertLastTextContent(capture.LastMessages![0]), expectedTtl: null);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "assistant one")
        ]);
        AssertCacheControl(AssertLastTextContent(capture.LastMessages![0]), expectedTtl: null);
        AssertCacheControl(AssertSingleTextContent(capture.LastMessages![1]), expectedTtl: null);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "assistant one"),
            new ChatMessage(ChatRole.Assistant, "assistant two")
        ]);

        AssertCacheControl(AssertLastTextContent(capture.LastMessages![0]), expectedTtl: null);
        AssertCacheControl(AssertSingleTextContent(capture.LastMessages![1]), expectedTtl: null);
        AssertCacheControl(AssertLastTextContent(capture.LastMessages![2]), expectedTtl: null);
    }

    [Fact]
    public async Task RollingBreakpoints_NewUserTurnKeepsPreviousAssistantAndMarksNewUser()
    {
        var capture = new CaptureChatClient();
        var client = CreateClient("claude-opus-4-1", capture: capture);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "assistant one")
        ]);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "assistant one"),
            new ChatMessage(ChatRole.User, "next request")
        ]);

        AssertCacheControl(AssertLastTextContent(capture.LastMessages![1]), expectedTtl: null);
        AssertCacheControl(AssertLastTextContent(capture.LastMessages![2]), expectedTtl: null);
    }

    [Fact]
    public async Task RollingBreakpoints_DropsRememberedPointsAfterCompactionChangesPrefix()
    {
        var capture = new CaptureChatClient();
        var client = CreateClient("claude-opus-4-1", capture: capture);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "assistant one")
        ]);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.System, "compacted summary"),
            new ChatMessage(ChatRole.User, "next request")
        ]);

        var systemText = AssertLastTextContent(capture.LastMessages![0]);
        Assert.Null(systemText.AdditionalProperties);
        AssertCacheControl(AssertLastTextContent(capture.LastMessages![1]), expectedTtl: null);
    }

    [Fact]
    public void OpenAIAdapter_AssistantTextWithFunctionCallMovesCacheControlToTextBlockAndPreservesToolCalls()
    {
        var client = CreateClient("claude-opus-4-1");
        var prepared = client.Prepare([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, (IList<AIContent>)[
                new TextContent("I will read that."),
                new FunctionCallContent("call_1", "ReadFile", new Dictionary<string, object?> { ["path"] = "a.txt" })
            ])
        ], null);

        var openAiMessage = OpenAI.Chat.MicrosoftExtensionsAIChatExtensions
            .AsOpenAIChatMessages(prepared.Messages, prepared.Options)
            .Last();
        var json = ModelReaderWriter.Write(openAiMessage).ToString();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.True(root.TryGetProperty("tool_calls", out var toolCalls));
        Assert.Equal(1, toolCalls.GetArrayLength());
        Assert.Equal("call_1", toolCalls[0].GetProperty("id").GetString());
        Assert.Equal("function", toolCalls[0].GetProperty("type").GetString());
        var function = toolCalls[0].GetProperty("function");
        Assert.Equal("ReadFile", function.GetProperty("name").GetString());
        Assert.Equal("""{"path":"a.txt"}""", function.GetProperty("arguments").GetString());
        Assert.Equal("I will read that.", root.GetProperty("content").GetString());
        Assert.Equal("ephemeral", root.GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());

        var rewritten = PromptCacheControlPipelinePolicy.RewriteJson(
            $$"""{"messages":[{{json}}]}""");
        Assert.NotNull(rewritten);
        using var rewrittenDocument = JsonDocument.Parse(rewritten);
        var message = rewrittenDocument.RootElement.GetProperty("messages")[0];
        Assert.False(message.TryGetProperty(PromptCachingChatClient.CacheControlKey, out _));
        Assert.True(message.TryGetProperty("tool_calls", out var rewrittenToolCalls));
        Assert.Equal(1, rewrittenToolCalls.GetArrayLength());
        Assert.Equal("call_1", rewrittenToolCalls[0].GetProperty("id").GetString());
        var content = message.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("I will read that.", content[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", content[0].GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());
    }

    [Fact]
    public async Task RollingBreakpoints_RestoresPreviousAssistantToolCallAndAddsNewUser()
    {
        var capture = new CaptureChatClient();
        var client = CreateClient("claude-opus-4-1", capture: capture);
        var assistant = new ChatMessage(ChatRole.Assistant, (IList<AIContent>)[
            new TextContent("I will read that."),
            new FunctionCallContent("call_1", "ReadFile", new Dictionary<string, object?> { ["path"] = "a.txt" })
        ]);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            assistant
        ]);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            assistant,
            new ChatMessage(ChatRole.Tool, (IList<AIContent>)[
                new FunctionResultContent("call_1", "file contents")
            ]),
            new ChatMessage(ChatRole.User, "continue")
        ]);

        AssertCacheControl(AssertSingleTextContent(capture.LastMessages![1]), expectedTtl: null);
        AssertCacheControl(AssertLastTextContent(capture.LastMessages![3]), expectedTtl: null);

        var openAiMessages = OpenAI.Chat.MicrosoftExtensionsAIChatExtensions
            .AsOpenAIChatMessages(capture.LastMessages!, null)
            .ToList();
        var assistantJson = ModelReaderWriter.Write(openAiMessages[1]).ToString();
        var rewritten = PromptCacheControlPipelinePolicy.RewriteJson(
            $$"""{"messages":[{{assistantJson}}]}""");
        Assert.NotNull(rewritten);
        using var rewrittenDocument = JsonDocument.Parse(rewritten);
        var message = rewrittenDocument.RootElement.GetProperty("messages")[0];
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.True(message.TryGetProperty("tool_calls", out var toolCalls));
        Assert.Equal(1, toolCalls.GetArrayLength());
        var content = message.GetProperty("content");
        Assert.Equal("I will read that.", content[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", content[0].GetProperty(PromptCachingChatClient.CacheControlKey).GetProperty("type").GetString());
    }

    [Fact]
    public async Task RollingBreakpoints_RestoresPreviousToolResultAndAddsNewUser()
    {
        var capture = new CaptureChatClient();
        var client = CreateClient("claude-opus-4-1", capture: capture);
        var tool = new ChatMessage(ChatRole.Tool, (IList<AIContent>)[
            new FunctionResultContent("call_1", (IList<AIContent>)[new TextContent("file contents")])
        ]);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "I will read that."),
            tool
        ]);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "I will read that."),
            tool,
            new ChatMessage(ChatRole.User, "continue")
        ]);

        AssertCacheControl(AssertLastTextContent(capture.LastMessages![1]), expectedTtl: null);
        var toolResult = Assert.IsType<FunctionResultContent>(Assert.Single(capture.LastMessages![2].Contents));
        AssertCacheControl(toolResult, expectedTtl: null);
        AssertCacheControl(AssertLastTextContent(capture.LastMessages![3]), expectedTtl: null);
    }

    [Fact]
    public async Task RollingBreakpoints_ContinuousEmptyAssistantToolLoopsAdvanceWithToolResult()
    {
        var capture = new CaptureChatClient();
        var client = CreateClient("claude-opus-4-1", capture: capture);
        var firstTool = new ChatMessage(ChatRole.Tool, (IList<AIContent>)[
            new FunctionResultContent("call_1", "first result")
        ]);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, (IList<AIContent>)[
                new FunctionCallContent("call_1", "ReadFile", new Dictionary<string, object?>())
            ]),
            firstTool
        ]);

        var secondTool = new ChatMessage(ChatRole.Tool, (IList<AIContent>)[
            new FunctionResultContent("call_2", "second result")
        ]);
        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, (IList<AIContent>)[
                new FunctionCallContent("call_1", "ReadFile", new Dictionary<string, object?>())
            ]),
            firstTool,
            new ChatMessage(ChatRole.Assistant, (IList<AIContent>)[
                new FunctionCallContent("call_2", "ReadFile", new Dictionary<string, object?>())
            ]),
            secondTool
        ]);

        var restoredTool = Assert.IsType<FunctionResultContent>(Assert.Single(capture.LastMessages![2].Contents));
        var latestTool = Assert.IsType<FunctionResultContent>(Assert.Single(capture.LastMessages![4].Contents));
        AssertCacheControl(restoredTool, expectedTtl: null);
        AssertCacheControl(latestTool, expectedTtl: null);
    }

    [Fact]
    public async Task GetResponseAsync_WhenTraceCollectorProvided_RecordsPromptCachePointSummaries()
    {
        const string sessionKey = "trace-cache";
        var store = new TraceStore();
        var collector = new TraceCollector(store);
        var client = CreateClient(
            "claude-opus-4-1",
            capture: new CaptureChatClient(),
            sessionKey: sessionKey,
            traceCollector: collector);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "secret prompt"),
            new ChatMessage(ChatRole.Tool, (IList<AIContent>)[
                new FunctionResultContent("call_1", "secret tool result")
            ])
        ]);

        var evt = Assert.Single(store.GetEvents(sessionKey), e => e.Type == TraceEventType.PromptCachePoint);
        Assert.DoesNotContain("secret prompt", evt.MetadataJson);
        Assert.DoesNotContain("secret tool result", evt.MetadataJson);

        using var document = JsonDocument.Parse(evt.MetadataJson!);
        var root = document.RootElement;
        Assert.Equal(sessionKey, root.GetProperty("sessionKey").GetString());
        Assert.Equal("claude-opus-4-1", root.GetProperty("model").GetString());
        var points = root.GetProperty("points");
        Assert.Equal(2, points.GetArrayLength());
        Assert.Equal("user", points[0].GetProperty("Role").GetString());
        Assert.Equal("text", points[0].GetProperty("ContentKind").GetString());
        Assert.Equal("tool", points[1].GetProperty("Role").GetString());
        Assert.Equal("function_result", points[1].GetProperty("ContentKind").GetString());
        Assert.True(points[1].GetProperty("Latest").GetBoolean());
    }

    [Fact]
    public async Task GetResponseAsync_ForNonClaudeOrDisabled_DoesNotRecordPromptCachePointTrace()
    {
        var nonClaudeStore = new TraceStore();
        var nonClaudeClient = CreateClient(
            "gpt-4o-mini",
            capture: new CaptureChatClient(),
            sessionKey: "non-claude",
            traceCollector: new TraceCollector(nonClaudeStore));
        await nonClaudeClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        Assert.DoesNotContain(nonClaudeStore.GetEvents("non-claude"), e => e.Type == TraceEventType.PromptCachePoint);

        var disabledStore = new TraceStore();
        var disabledClient = new PromptCachingChatClient(
            new CaptureChatClient(),
            new AppConfig.PromptCachingConfig { Enabled = false },
            "claude-opus-4-1",
            new TraceCollector(disabledStore),
            () => "disabled");
        await disabledClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        Assert.DoesNotContain(disabledStore.GetEvents("disabled"), e => e.Type == TraceEventType.PromptCachePoint);
    }

    private static PromptCachingChatClient CreateClient(
        string model,
        string ttl = "",
        CaptureChatClient? capture = null,
        string? sessionKey = null,
        TraceCollector? traceCollector = null)
    {
        var key = sessionKey ?? Guid.NewGuid().ToString("N");
        return new(
            capture ?? new CaptureChatClient(),
            new AppConfig.PromptCachingConfig { Ttl = ttl },
            model,
            traceCollector,
            sessionKeyAccessor: () => key);
    }

    private static void AssertCacheControl(AIContent content, string? expectedTtl)
    {
        Assert.NotNull(content.AdditionalProperties);
        var cacheControl = Assert.IsType<Dictionary<string, object>>(content.AdditionalProperties![PromptCachingChatClient.CacheControlKey]);
        Assert.Equal("ephemeral", cacheControl["type"]);
        if (expectedTtl is null)
        {
            Assert.False(cacheControl.ContainsKey("ttl"));
        }
        else
        {
            Assert.Equal(expectedTtl, cacheControl["ttl"]);
        }
    }

    private static TextContent AssertLastTextContent(ChatMessage message) =>
        Assert.IsType<TextContent>(message.Contents.Last());

    private static TextContent AssertSingleTextContent(ChatMessage message) =>
        Assert.Single(message.Contents.OfType<TextContent>());

    private sealed class CaptureChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
            await Task.CompletedTask;
            yield break;
        }
    }
}
