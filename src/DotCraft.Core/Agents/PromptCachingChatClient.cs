using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCraft.Configuration;
using DotCraft.Tracing;
using Microsoft.Extensions.AI;
using OpenAIAssistantChatMessage = OpenAI.Chat.AssistantChatMessage;
using OpenAIChatMessageContentPart = OpenAI.Chat.ChatMessageContentPart;
using OpenAIChatMessage = OpenAI.Chat.ChatMessage;
using OpenAISystemChatMessage = OpenAI.Chat.SystemChatMessage;
using OpenAIToolChatMessage = OpenAI.Chat.ToolChatMessage;
using OpenAIUserChatMessage = OpenAI.Chat.UserChatMessage;

namespace DotCraft.Agents;

/// <summary>
/// Adds Anthropic/LiteLLM prompt-cache markers to OpenAI-compatible Claude requests.
/// </summary>
public sealed class PromptCachingChatClient(
    IChatClient innerClient,
    AppConfig.PromptCachingConfig config,
    string model,
    TraceCollector? traceCollector = null,
    Func<string?>? sessionKeyAccessor = null) : DelegatingChatClient(innerClient)
{
    internal const string CacheControlKey = "cache_control";
    private const int MaxCacheBreakpoints = 4;
    private const string DefaultSessionKey = "__default__";

    private readonly ConcurrentDictionary<string, CachePointState> _cachePointStates = new();
    private readonly Func<string?> _sessionKeyAccessor = sessionKeyAccessor ?? TracingChatClient.GetActiveSessionKey;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(chatMessages, options);
        RecordCachePoints(prepared);
        var response = await base.GetResponseAsync(prepared.Messages, prepared.Options, cancellationToken);
        CommitCachePoints(prepared);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(chatMessages, options);
        RecordCachePoints(prepared);
        await foreach (var update in base.GetStreamingResponseAsync(prepared.Messages, prepared.Options, cancellationToken))
        {
            yield return update;
        }

        CommitCachePoints(prepared);
    }

    internal (
        IReadOnlyList<ChatMessage> Messages,
        ChatOptions? Options,
        IReadOnlyList<PendingCachePoint> PendingCachePoints,
        string? SessionKey) Prepare(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options)
    {
        var messages = chatMessages as IReadOnlyList<ChatMessage> ?? chatMessages.ToList();
        if (!config.ShouldApply(model))
            return (messages, options, [], null);

        var preparedMessages = new List<ChatMessage>(messages.Count + 1);
        var preparedOptions = options;
        var cacheControl = CreateCacheControl();
        var sessionKey = ResolveSessionKey();

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            preparedOptions = options.Clone();
            preparedOptions.Instructions = null;
            preparedMessages.Add(new ChatMessage(
                ChatRole.System,
                (IList<AIContent>)[new TextContent(options.Instructions!)]));
        }

        foreach (var message in messages)
            preparedMessages.Add(message);

        var candidates = BuildCachePointCandidates(preparedMessages);
        var selected = SelectCachePoints(sessionKey, candidates);
        ApplyCacheControl(preparedMessages, selected, cacheControl);

        return (preparedMessages, preparedOptions, selected.Select(point => new PendingCachePoint(
            point.Candidate.Hash,
            new PromptCachePointTraceEntry(
                model,
                point.Candidate.Role.Value,
                point.Candidate.MessageIndex,
                point.Candidate.ContentIndex,
                point.Candidate.Sequence,
                point.Candidate.Hash[..Math.Min(12, point.Candidate.Hash.Length)],
                point.Remembered,
                point.Latest,
                point.Candidate.ContentKind))).ToArray(), sessionKey);
    }

    private Dictionary<string, object> CreateCacheControl()
    {
        var cacheControl = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["type"] = "ephemeral"
        };

        if (!string.IsNullOrWhiteSpace(config.Ttl))
            cacheControl["ttl"] = config.Ttl.Trim();

        return cacheControl;
    }

    private string ResolveSessionKey()
    {
        var sessionKey = _sessionKeyAccessor();
        return string.IsNullOrWhiteSpace(sessionKey)
            ? DefaultSessionKey
            : sessionKey.Trim();
    }

    private List<SelectedCachePoint> SelectCachePoints(
        string sessionKey,
        IReadOnlyList<CachePointCandidate> candidates)
    {
        if (candidates.Count == 0)
            return [];

        var state = _cachePointStates.GetOrAdd(sessionKey, static _ => new CachePointState());
        var remembered = state.GetHashes();

        var selected = new Dictionary<string, SelectedCachePoint>(StringComparer.Ordinal);
        AddLatest(selected, candidates, remembered, ChatRole.User);
        AddLatest(selected, candidates, remembered, ChatRole.Assistant);
        AddLatest(selected, candidates, remembered, ChatRole.Tool);

        if (selected.Count == 0)
            AddLatest(selected, candidates, remembered, ChatRole.System);

        foreach (var candidate in candidates
                     .Where(candidate => remembered.Contains(candidate.Hash))
                     .OrderByDescending(candidate => candidate.Sequence))
        {
            if (selected.TryGetValue(candidate.Hash, out var existing))
                selected[candidate.Hash] = existing with { Remembered = true };
            else
                selected[candidate.Hash] = new SelectedCachePoint(candidate, Remembered: true, Latest: false);

            if (selected.Count >= MaxCacheBreakpoints)
                break;
        }

        return selected.Values
            .OrderBy(static point => point.Candidate.Sequence)
            .ToList();
    }

    private static void AddLatest(
        Dictionary<string, SelectedCachePoint> selected,
        IReadOnlyList<CachePointCandidate> candidates,
        HashSet<string> remembered,
        ChatRole role)
    {
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var candidate = candidates[i];
            if (candidate.Role == role)
            {
                if (selected.TryGetValue(candidate.Hash, out var existing))
                {
                    selected[candidate.Hash] = existing with { Latest = true };
                }
                else
                {
                    selected[candidate.Hash] = new SelectedCachePoint(
                        candidate,
                        remembered.Contains(candidate.Hash),
                        Latest: true);
                }

                return;
            }
        }
    }

    private void RecordCachePoints(
        (IReadOnlyList<ChatMessage> Messages,
            ChatOptions? Options,
            IReadOnlyList<PendingCachePoint> PendingCachePoints,
            string? SessionKey) prepared)
    {
        if (traceCollector == null ||
            prepared.SessionKey == null ||
            prepared.PendingCachePoints.Count == 0)
        {
            return;
        }

        traceCollector.RecordPromptCachePoints(
            prepared.SessionKey,
            model,
            prepared.PendingCachePoints.Select(static point => point.Trace).ToArray());
    }

    private void CommitCachePoints(
        (IReadOnlyList<ChatMessage> Messages,
            ChatOptions? Options,
            IReadOnlyList<PendingCachePoint> PendingCachePoints,
            string? SessionKey) prepared)
    {
        if (prepared.SessionKey == null || prepared.PendingCachePoints.Count == 0)
            return;

        var state = _cachePointStates.GetOrAdd(prepared.SessionKey, static _ => new CachePointState());
        state.Replace(prepared.PendingCachePoints.Select(static point => point.Hash));
    }

    private static void ApplyCacheControl(
        List<ChatMessage> messages,
        IReadOnlyList<SelectedCachePoint> cachePoints,
        Dictionary<string, object> cacheControl)
    {
        var replacements = new Dictionary<int, IReadOnlyList<ChatMessage>>();
        foreach (var group in cachePoints.GroupBy(static point => point.Candidate.MessageIndex))
        {
            var message = messages[group.Key];
            var targetIndexes = group.Select(static point => point.Candidate.ContentIndex).ToHashSet();
            if (message.Role == ChatRole.Tool &&
                TryCreateCachedToolMessages(message, targetIndexes, cacheControl, out var toolMessages))
            {
                replacements[group.Key] = toolMessages;
            }
            else if (TryCreateCachedTextMessage(message, targetIndexes, cacheControl, out var cachedMessage))
            {
                replacements[group.Key] = [cachedMessage];
            }
        }

        if (replacements.Count == 0)
            return;

        var rewritten = new List<ChatMessage>(messages.Count + replacements.Values.Sum(static value => value.Count) - replacements.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (replacements.TryGetValue(i, out var replacement))
                rewritten.AddRange(replacement);
            else
                rewritten.Add(messages[i]);
        }

        messages.Clear();
        messages.AddRange(rewritten);
    }

    private static IReadOnlyList<CachePointCandidate> BuildCachePointCandidates(
        IReadOnlyList<ChatMessage> messages)
    {
        var candidates = new List<CachePointCandidate>();
        var canonical = new StringBuilder();
        var sequence = 0;

        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            var cacheableRole = IsCacheableRole(message);
            AppendMessageBoundary(canonical, message);

            for (var contentIndex = 0; contentIndex < message.Contents.Count; contentIndex++)
            {
                var content = message.Contents[contentIndex];
                AppendContent(canonical, content);

                if (cacheableRole && TryGetCachePointContentKind(content, out var contentKind))
                {
                    candidates.Add(new CachePointCandidate(
                        messageIndex,
                        contentIndex,
                        message.Role,
                        sequence++,
                        ComputeHash(canonical),
                        contentKind));
                }
            }
        }

        return candidates;
    }

    private static bool IsCacheableRole(ChatMessage message)
    {
        if (message.Role == ChatRole.User || message.Role == ChatRole.System)
            return true;

        if (message.Role == ChatRole.Assistant)
            return message.Contents.Any(static content => content is TextContent { Text.Length: > 0 });

        return message.Role == ChatRole.Tool;
    }

    private static bool TryGetCachePointContentKind(AIContent content, out string contentKind)
    {
        if (content is TextContent { Text.Length: > 0 })
        {
            contentKind = "text";
            return true;
        }

        if (content is FunctionResultContent result &&
            TryGetToolResultWireText(result, out var text) &&
            !string.IsNullOrEmpty(text))
        {
            contentKind = "function_result";
            return true;
        }

        contentKind = string.Empty;
        return false;
    }

    private static void AppendMessageBoundary(StringBuilder builder, ChatMessage message)
    {
        builder.Append("\nmessage:");
        builder.Append(message.Role.Value);
        builder.Append(':');
        builder.Append(message.AuthorName ?? string.Empty);
    }

    private static void AppendContent(StringBuilder builder, AIContent content)
    {
        builder.Append("\ncontent:");
        builder.Append(content.GetType().FullName);
        builder.Append(':');

        switch (content)
        {
            case TextContent text:
                AppendString(builder, text.Text);
                break;
            case FunctionCallContent call:
                AppendString(builder, call.CallId);
                AppendString(builder, call.Name);
                AppendCanonicalObject(builder, call.Arguments);
                break;
            case FunctionResultContent result:
                AppendString(builder, result.CallId);
                if (TryGetToolResultWireText(result, out var toolResultText))
                    AppendString(builder, toolResultText);
                else
                    AppendCanonicalObject(builder, result.Result);
                if (result.Exception != null)
                    AppendString(builder, result.Exception.GetType().FullName + ":" + result.Exception.Message);
                break;
            case DataContent data:
                AppendString(builder, data.MediaType);
                builder.Append(data.Data.Length);
                break;
            default:
                AppendString(builder, content.ToString());
                break;
        }
    }

    private static void AppendString(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }

    private static void AppendCanonicalObject(StringBuilder builder, object? value)
    {
        if (value == null)
        {
            builder.Append("null;");
            return;
        }

        if (value is string text)
        {
            AppendString(builder, text);
            return;
        }

        try
        {
            builder.Append(JsonSerializer.Serialize(value));
            builder.Append(';');
        }
        catch (NotSupportedException)
        {
            AppendString(builder, value.ToString());
        }
    }

    private static string ComputeHash(StringBuilder canonical)
    {
        var bytes = Encoding.UTF8.GetBytes(canonical.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool TryCreateCachedTextMessage(
        ChatMessage message,
        HashSet<int> targetIndexes,
        Dictionary<string, object> cacheControl,
        out ChatMessage cachedMessage)
    {
        cachedMessage = message;
        var contents = new List<AIContent>(message.Contents.Count);
        var markedAny = false;

        for (var i = 0; i < message.Contents.Count; i++)
        {
            var content = message.Contents[i];
            if (targetIndexes.Contains(i) && content is TextContent text)
            {
                contents.Add(CreateCachedTextContent(text, cacheControl));
                markedAny = true;
            }
            else
            {
                contents.Add(content);
            }
        }

        if (!markedAny)
            return false;

        cachedMessage = new ChatMessage(message.Role, contents)
        {
            AdditionalProperties = message.AdditionalProperties,
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            MessageId = message.MessageId,
            RawRepresentation = message.RawRepresentation
        };

        if (message.Role == ChatRole.Assistant &&
            message.Contents.Any(static content => content is FunctionCallContent) &&
            targetIndexes.Count == 1 &&
            contents.Count(static content => content is TextContent) == 1 &&
            contents[targetIndexes.Single()] is TextContent assistantText)
        {
            cachedMessage.RawRepresentation = CreateCachedAssistantToolCallMessage(message, assistantText.Text, cacheControl);
        }
        else if (contents.Count == 1 && contents[0] is TextContent textContent)
        {
            cachedMessage.RawRepresentation = CreateCachedRootMessage(message.Role, textContent.Text, cacheControl);
        }

        return true;
    }

    private static bool TryCreateCachedToolMessages(
        ChatMessage message,
        HashSet<int> targetIndexes,
        Dictionary<string, object> cacheControl,
        out IReadOnlyList<ChatMessage> cachedMessages)
    {
        cachedMessages = [];
        var messages = new List<ChatMessage>(message.Contents.Count);
        var markedAny = false;

        for (var i = 0; i < message.Contents.Count; i++)
        {
            if (message.Contents[i] is not FunctionResultContent result)
                return false;

            AIContent toolContent = result;
            if (targetIndexes.Contains(i))
            {
                if (!TryCreateCachedFunctionResultContent(result, cacheControl, out var cachedResult))
                    return false;

                toolContent = cachedResult;
                markedAny = true;
            }

            messages.Add(new ChatMessage(ChatRole.Tool, (IList<AIContent>)[toolContent])
            {
                AdditionalProperties = message.AdditionalProperties,
                AuthorName = message.AuthorName,
                CreatedAt = message.CreatedAt,
                MessageId = message.MessageId,
                RawRepresentation = toolContent.RawRepresentation
            });
        }

        if (!markedAny)
            return false;

        cachedMessages = messages;
        return true;
    }

    private static TextContent CreateCachedTextContent(
        TextContent text,
        Dictionary<string, object> cacheControl) =>
        new(text.Text)
        {
            AdditionalProperties = WithCacheControl(text.AdditionalProperties, cacheControl),
            RawRepresentation = CreateCachedTextPart(text.Text, cacheControl)
        };

    private static bool TryCreateCachedFunctionResultContent(
        FunctionResultContent result,
        Dictionary<string, object> cacheControl,
        out FunctionResultContent cachedResult)
    {
        cachedResult = result;
        if (!TryGetToolResultWireText(result, out var text))
            return false;

        cachedResult = new FunctionResultContent(result.CallId, result.Result)
        {
            AdditionalProperties = WithCacheControl(result.AdditionalProperties, cacheControl),
            Exception = result.Exception,
            RawRepresentation = CreateCachedToolRootMessage(result.CallId, text, cacheControl)
        };
        return true;
    }

    private static bool TryGetToolResultWireText(FunctionResultContent result, out string text)
    {
        if (result.Result is string value)
        {
            text = value;
            return true;
        }

        if (result.Result is IEnumerable<AIContent> contents)
        {
            var textContents = new List<AIContent>();

            foreach (var content in contents)
            {
                if (content is not TextContent)
                {
                    text = string.Empty;
                    return false;
                }

                textContents.Add(content);
            }

            text = textContents.Count == 0
                ? string.Empty
                : JsonSerializer.Serialize(textContents, AIJsonUtilities.DefaultOptions);
            return text.Length > 0;
        }

        text = string.Empty;
        return false;
    }

    private static AdditionalPropertiesDictionary WithCacheControl(
        AdditionalPropertiesDictionary? source,
        Dictionary<string, object> cacheControl)
    {
        var properties = source == null
            ? new AdditionalPropertiesDictionary()
            : new AdditionalPropertiesDictionary(source);
        properties[CacheControlKey] = cacheControl;
        return properties;
    }

    private static OpenAIChatMessageContentPart CreateCachedTextPart(
        string? text,
        Dictionary<string, object> cacheControl)
    {
        var part = OpenAIChatMessageContentPart.CreateTextPart(text ?? string.Empty);
#pragma warning disable SCME0001
        part.Patch.Set(
            "$.cache_control"u8,
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(cacheControl)));
#pragma warning restore SCME0001
        return part;
    }

    private static OpenAIChatMessage? CreateCachedRootMessage(
        ChatRole role,
        string? text,
        Dictionary<string, object> cacheControl)
    {
        OpenAIChatMessage? message = role switch
        {
            var value when value == ChatRole.User => new OpenAIUserChatMessage(text ?? string.Empty),
            var value when value == ChatRole.Assistant => new OpenAIAssistantChatMessage(text ?? string.Empty),
            var value when value == ChatRole.System => new OpenAISystemChatMessage(text ?? string.Empty),
            _ => null
        };

        if (message == null)
            return null;

#pragma warning disable SCME0001
        message.Patch.Set(
            "$.cache_control"u8,
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(cacheControl)));
#pragma warning restore SCME0001
        return message;
    }

    private static OpenAIChatMessage CreateCachedToolRootMessage(
        string toolCallId,
        string text,
        Dictionary<string, object> cacheControl)
    {
        var message = new OpenAIToolChatMessage(toolCallId, text);

#pragma warning disable SCME0001
        message.Patch.Set(
            "$.cache_control"u8,
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(cacheControl)));
#pragma warning restore SCME0001
        return message;
    }

    private static OpenAIChatMessage CreateCachedAssistantToolCallMessage(
        ChatMessage message,
        string? text,
        Dictionary<string, object> cacheControl)
    {
        var assistantMessage = new OpenAIAssistantChatMessage(text ?? string.Empty);

#pragma warning disable SCME0001
        assistantMessage.Patch.Set(
            "$.cache_control"u8,
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(cacheControl)));
        assistantMessage.Patch.Set(
            "$.tool_calls"u8,
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(CreateOpenAIToolCalls(message))));
#pragma warning restore SCME0001

        return assistantMessage;
    }

    private static object[] CreateOpenAIToolCalls(ChatMessage message) =>
        message.Contents
            .OfType<FunctionCallContent>()
            .Select(static call => new
            {
                id = call.CallId,
                type = "function",
                function = new
                {
                    name = call.Name,
                    arguments = SerializeToolCallArguments(call.Arguments)
                }
            })
            .Cast<object>()
            .ToArray();

    private static string SerializeToolCallArguments(object? arguments) =>
        arguments == null
            ? "{}"
            : JsonSerializer.Serialize(arguments);

    internal sealed record PendingCachePoint(string Hash, PromptCachePointTraceEntry Trace);

    private sealed record SelectedCachePoint(
        CachePointCandidate Candidate,
        bool Remembered,
        bool Latest);

    private sealed record CachePointCandidate(
        int MessageIndex,
        int ContentIndex,
        ChatRole Role,
        int Sequence,
        string Hash,
        string ContentKind);

    private sealed class CachePointState
    {
        private readonly Lock _gate = new();
        private HashSet<string> _hashes = new(StringComparer.Ordinal);

        public HashSet<string> GetHashes()
        {
            lock (_gate)
                return new HashSet<string>(_hashes, StringComparer.Ordinal);
        }

        public void Replace(IEnumerable<string> hashes)
        {
            lock (_gate)
                _hashes = hashes.Take(MaxCacheBreakpoints).ToHashSet(StringComparer.Ordinal);
        }
    }
}
