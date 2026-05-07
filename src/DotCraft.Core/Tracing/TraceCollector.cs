using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DotCraft.Tools;
using Microsoft.Extensions.AI;

namespace DotCraft.Tracing;

public sealed class TraceCollector(TraceStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void RecordRequest(string sessionKey, string prompt)
    {
        store.Record(new TraceEvent
        {
            Type = TraceEventType.Request,
            SessionKey = sessionKey,
            Content = prompt
        });
    }

    public void RecordSessionMetadata(string sessionKey, string? finalSystemPrompt, IEnumerable<string>? toolNames)
    {
        var normalizedToolNames = NormalizeToolNames(toolNames);
        var existing = store.GetSession(sessionKey);
        var previousSystemPromptHash = existing?.SystemPromptHash;
        var previousToolSchemaHash = existing?.ToolSchemaHash;
        var systemPromptHash = ComputeHash(finalSystemPrompt);
        var toolSchemaHash = ComputeHash(string.Join("\n", normalizedToolNames));
        var hasBaseline =
            !string.IsNullOrWhiteSpace(previousSystemPromptHash)
            || !string.IsNullOrWhiteSpace(previousToolSchemaHash);

        string eventKind;
        var changedFields = new List<string>(capacity: 2);
        string[] changedToolNames = [];
        if (!hasBaseline)
        {
            eventKind = PromptCacheEventKinds.Baseline;
        }
        else
        {
            var promptChanged =
                !string.Equals(previousSystemPromptHash, systemPromptHash, StringComparison.Ordinal);
            var toolsChanged =
                !string.Equals(previousToolSchemaHash, toolSchemaHash, StringComparison.Ordinal);

            if (!promptChanged && !toolsChanged)
                return;

            if (promptChanged)
                changedFields.Add(PromptCacheChangedFields.Prompt);
            if (toolsChanged)
                changedFields.Add(PromptCacheChangedFields.Tools);

            changedToolNames = GetAppendedToolNames(existing?.ToolNames ?? [], normalizedToolNames);
            var toolsAppendOnly = toolsChanged
                && changedToolNames.Length > 0
                && IsAppendOnly(existing?.ToolNames ?? [], normalizedToolNames);
            eventKind = !promptChanged && toolsAppendOnly
                ? PromptCacheEventKinds.ToolExtension
                : PromptCacheEventKinds.Drift;
        }

        if (eventKind == PromptCacheEventKinds.Baseline && string.IsNullOrWhiteSpace(systemPromptHash) && string.IsNullOrWhiteSpace(toolSchemaHash))
            return;

        store.Record(new TraceEvent
        {
            Type = TraceEventType.SessionMetadata,
            SessionKey = sessionKey,
            FinalSystemPrompt = finalSystemPrompt,
            ToolNames = normalizedToolNames,
            SystemPromptHash = systemPromptHash,
            ToolSchemaHash = toolSchemaHash,
            PromptDriftDetected = eventKind == PromptCacheEventKinds.Drift,
            PromptCacheEventKind = eventKind,
            PromptCacheChangedFields = changedFields.ToArray(),
            PreviousSystemPromptHash = previousSystemPromptHash,
            PreviousToolSchemaHash = previousToolSchemaHash,
            CurrentSystemPromptHash = systemPromptHash,
            CurrentToolSchemaHash = toolSchemaHash,
            ChangedToolNames = changedToolNames
        });
    }

    public void RecordResponse(string sessionKey, string? response, DateTimeOffset? timestamp = null)
    {
        store.Record(new TraceEvent
        {
            Type = TraceEventType.Response,
            SessionKey = sessionKey,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Content = response ?? "(empty)"
        });
    }

    public void RecordResponse(
        string sessionKey,
        string? response,
        string? responseId,
        string? messageId,
        string? modelId,
        string? finishReason,
        object? metadata = null,
        DateTimeOffset? timestamp = null)
    {
        store.Record(new TraceEvent
        {
            Type = TraceEventType.Response,
            SessionKey = sessionKey,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Content = response ?? "(empty)",
            ResponseId = responseId,
            MessageId = messageId,
            ModelId = modelId,
            FinishReason = finishReason,
            MetadataJson = SerializeMetadata(metadata)
        });
    }

    public void RecordToolCallStarted(string sessionKey, FunctionCallContent fc)
    {
        string? argsJson = null;
        if (fc.Arguments != null)
        {
            try
            {
                argsJson = JsonSerializer.Serialize(fc.Arguments, JsonOptions);
            }
            catch
            {
                argsJson = fc.Arguments.ToString();
            }
        }

        store.Record(new TraceEvent
        {
            Type = TraceEventType.ToolCallStarted,
            SessionKey = sessionKey,
            ToolName = fc.Name,
            ToolIcon = ToolRegistry.GetToolIcon(fc.Name),
            ToolArguments = argsJson,
            Content = fc.CallId,
            CallId = fc.CallId
        });
    }

    public void RecordToolCallCompleted(string sessionKey, FunctionResultContent fr, string? toolName, double durationMs)
    {
        var result = Agents.ImageContentSanitizingChatClient.DescribeResult(fr.Result);
        store.Record(new TraceEvent
        {
            Type = TraceEventType.ToolCallCompleted,
            SessionKey = sessionKey,
            ToolName = toolName ?? "unknown",
            ToolIcon = ToolRegistry.GetToolIcon(toolName ?? ""),
            ToolResult = result,
            DurationMs = durationMs,
            Content = fr.CallId,
            CallId = fr.CallId
        });
    }

    public void RecordToolInjection(string sessionKey, IReadOnlyList<string> toolNames)
    {
        var normalizedToolNames = NormalizeToolNames(toolNames);
        store.Record(new TraceEvent
        {
            Type = TraceEventType.ToolInjection,
            SessionKey = sessionKey,
            ToolName = $"{normalizedToolNames.Length} tool{(normalizedToolNames.Length != 1 ? "s" : "")} injected",
            ToolIcon = "🔌",
            Content = string.Join(", ", normalizedToolNames),
            PromptCacheEventKind = PromptCacheEventKinds.ToolExtension,
            PromptCacheChangedFields = [PromptCacheChangedFields.Tools],
            ChangedToolNames = normalizedToolNames
        });
    }

    public void RecordPromptCachePoints(
        string sessionKey,
        string model,
        IReadOnlyList<PromptCachePointTraceEntry> points)
    {
        if (points.Count == 0)
            return;

        store.Record(new TraceEvent
        {
            Type = TraceEventType.PromptCachePoint,
            SessionKey = sessionKey,
            Content = $"{points.Count} prompt cache point{(points.Count == 1 ? "" : "s")}",
            ModelId = model,
            MetadataJson = JsonSerializer.Serialize(new
            {
                sessionKey,
                model,
                points
            }, JsonOptions)
        });
    }

    public void RecordTokenUsage(string sessionKey, long inputTokens, long outputTokens)
        => RecordTokenUsage(sessionKey, new TokenUsageSnapshot(inputTokens, outputTokens, 0, 0));

    public void RecordTokenUsage(string sessionKey, TokenUsageSnapshot usage)
    {
        store.Record(new TraceEvent
        {
            Type = TraceEventType.TokenUsage,
            SessionKey = sessionKey,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CachedInputTokens = usage.CachedInputTokens,
            CacheWriteInputTokens = usage.CacheWriteInputTokens,
            FreshInputTokens = usage.FreshInputTokens,
            NonCachedInputTokens = usage.NonCachedInputTokens,
            ReasoningOutputTokens = usage.ReasoningOutputTokens,
            TotalTokens = usage.TotalTokens
        });
    }

    public void RecordError(string sessionKey, string error)
    {
        store.Record(new TraceEvent
        {
            Type = TraceEventType.Error,
            SessionKey = sessionKey,
            Content = error
        });
    }

    public void RecordContextCompaction(string sessionKey)
    {
        store.Record(new TraceEvent
        {
            Type = TraceEventType.ContextCompaction,
            SessionKey = sessionKey,
            Content = "Context compacted due to token limit"
        });
    }

    public void RecordThinking(string sessionKey, string content, DateTimeOffset? timestamp = null)
    {
        store.Record(new TraceEvent
        {
            Type = TraceEventType.Thinking,
            SessionKey = sessionKey,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Content = content
        });
    }

    public void BindThreadMainSession(string threadId, DateTimeOffset? createdAt = null)
        => store.BindThreadMainSession(threadId, createdAt);

    public void BindChildSession(
        string sessionKey,
        string rootThreadId,
        string parentSessionKey,
        DateTimeOffset? createdAt = null)
        => store.BindChildSession(sessionKey, rootThreadId, parentSessionKey, createdAt);

    public string? ResolveRootThreadId(string sessionKey)
        => store.DescribeSessionDeletion(sessionKey).RootThreadId;

    public int GetTokenUsageCount(string sessionKey)
        => store.GetSession(sessionKey)?.TokenUsageCount ?? 0;

    public ToolCallTimer StartToolTimer()
    {
        return new ToolCallTimer();
    }

    private static string? SerializeMetadata(object? metadata)
    {
        if (metadata == null)
            return null;

        try
        {
            return JsonSerializer.Serialize(metadata, JsonOptions);
        }
        catch
        {
            return metadata.ToString();
        }
    }

    private static string[] NormalizeToolNames(IEnumerable<string>? toolNames)
    {
        if (toolNames == null)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return toolNames
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Where(seen.Add)
            .ToArray();
    }

    private static bool IsAppendOnly(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        if (previous.Count == 0 || current.Count <= previous.Count)
            return false;

        for (var i = 0; i < previous.Count; i++)
        {
            if (!string.Equals(previous[i], current[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string[] GetAppendedToolNames(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        if (!IsAppendOnly(previous, current))
            return [];

        return current.Skip(previous.Count).ToArray();
    }

    private static string? ComputeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class ToolCallTimer
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public double ElapsedMs => _stopwatch.Elapsed.TotalMilliseconds;

    public void Stop() => _stopwatch.Stop();
}
