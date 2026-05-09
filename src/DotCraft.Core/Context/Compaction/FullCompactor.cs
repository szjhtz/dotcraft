using DotCraft.Context;
using DotCraft.Tracing;
using Microsoft.Extensions.AI;

namespace DotCraft.Context.Compaction;

/// <summary>
/// Result of a full-history compaction run.
/// </summary>
public sealed record FullCompactResult(
    IReadOnlyList<ChatMessage> SummarizedMessages,
    IReadOnlyList<ChatMessage> PreservedTail,
    string FormattedSummary,
    string RawSummary,
    int EstimatedTokensBefore,
    int EstimatedTokensAfter);

/// <summary>
/// Result envelope for a full compaction attempt.
/// </summary>
public sealed record FullCompactAttempt(FullCompactResult? Result, string? Reason)
{
    public static FullCompactAttempt Succeeded(FullCompactResult result) => new(result, null);

    public static FullCompactAttempt Unavailable(string reason) => new(null, reason);
}

/// <summary>
/// Manual fallback compactor that summarizes the entire visible history when
/// there is no older prefix for partial compaction to replace.
/// </summary>
public sealed class FullCompactor
{
    private const int MaxPromptTooLongRetries = 3;
    private readonly IChatClient _chatClient;
    private readonly MaintenanceForkRunner? _maintenanceForkRunner;
    private readonly TraceCollector? _traceCollector;

    public FullCompactor(
        IChatClient chatClient,
        MaintenanceForkRunner? maintenanceForkRunner = null,
        TraceCollector? traceCollector = null)
    {
        _chatClient = chatClient;
        _maintenanceForkRunner = maintenanceForkRunner;
        _traceCollector = traceCollector;
    }

    /// <summary>
    /// Summarizes the whole history and returns a single continuation summary
    /// message. The snapshot fork is used only when it covers the supplied
    /// history; manual snapshots often predate the latest assistant response.
    /// </summary>
    public async Task<FullCompactAttempt> CompactAsync(
        IReadOnlyList<ChatMessage> messages,
        PromptRequestSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        return await CompactAsync(messages, snapshot, threadId: null, cancellationToken);
    }

    public async Task<FullCompactAttempt> CompactAsync(
        IReadOnlyList<ChatMessage> messages,
        PromptRequestSnapshot? snapshot,
        string? threadId,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return FullCompactAttempt.Unavailable("empty_history");

        var sanitized = SanitizeForSummary(messages);
        var paired = MessageGrouper.EnsurePairing(sanitized);
        if (paired.Count == 0)
            return FullCompactAttempt.Unavailable("no_summarizable_history");

        string? rawSummary;
        if (snapshot is not null
            && _maintenanceForkRunner is not null
            && TryBuildSnapshotTail(snapshot, sanitized, out var snapshotTail))
        {
            var fork = await RunSnapshotForkAsync(snapshot, snapshotTail, cancellationToken);
            if (fork.FallbackReason is not null)
                return FullCompactAttempt.Unavailable(fork.FallbackReason);

            rawSummary = fork.Text;
        }
        else
        {
            rawSummary = await RunLegacySummaryAsync(paired, threadId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(rawSummary))
            return FullCompactAttempt.Unavailable("summary_unavailable");

        var formatted = CompactionPrompts.GetCompactUserSummaryMessage(
            rawSummary,
            transcriptPath: null,
            recentMessagesPreserved: false);
        var replacement = new ChatMessage(ChatRole.Assistant, formatted);

        return FullCompactAttempt.Succeeded(new FullCompactResult(
            SummarizedMessages: messages,
            PreservedTail: [],
            FormattedSummary: formatted,
            RawSummary: rawSummary,
            EstimatedTokensBefore: MessageTokenEstimator.Estimate(messages),
            EstimatedTokensAfter: MessageTokenEstimator.Estimate([replacement])));
    }

    private async Task<MaintenanceForkResult> RunSnapshotForkAsync(
        PromptRequestSnapshot snapshot,
        IReadOnlyList<ChatMessage> messagesBeforeTask,
        CancellationToken cancellationToken)
    {
        var result = await _maintenanceForkRunner!.RunAsync(
            snapshot,
            new MaintenanceForkTask(
                MaintenanceForkTaskKind.ContextCompaction,
                BuildFullContextCompactionTaskInstructions()),
            messagesBeforeTask,
            cancellationToken);

        return result;
    }

    private async Task<string?> RunLegacySummaryAsync(
        IReadOnlyList<ChatMessage> messages,
        string? threadId,
        CancellationToken cancellationToken)
    {
        return await RunLegacySummaryWithRetriesAsync(messages, threadId, cancellationToken);
    }

    private async Task<string?> RunLegacySummaryWithRetriesAsync(
        IReadOnlyList<ChatMessage> messages,
        string? threadId,
        CancellationToken cancellationToken)
    {
        var candidate = messages.ToList();
        for (var attempt = 0; attempt <= MaxPromptTooLongRetries; attempt++)
        {
            try
            {
                return await RunLegacySummaryOnceAsync(candidate, threadId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (CompactionErrors.IsPromptTooLong(ex))
            {
                if (attempt == MaxPromptTooLongRetries)
                    return null;

                candidate = CompactionMessageTruncator.TruncateOldestGroups(candidate);
                if (candidate.Count == 0)
                    return null;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private async Task<string?> RunLegacySummaryOnceAsync(
        IReadOnlyList<ChatMessage> messages,
        string? threadId,
        CancellationToken cancellationToken)
    {
        var summaryPrompt = BuildFullContextCompactionTaskInstructions();
        var summaryMessages = new List<ChatMessage>(messages.Count + 1)
        {
            new(ChatRole.System, summaryPrompt)
        };
        summaryMessages.AddRange(messages);

        var sessionKey = CompactionTrace.ResolveSessionKey(threadId);
        _traceCollector?.RecordMaintenanceForkRequest(
            sessionKey,
            MaintenanceForkTaskKind.ContextCompaction,
            summaryPrompt,
            threadId,
            turnId: null,
            mode: "legacy",
            modelId: null,
            providerId: null,
            snapshotMessageCount: messages.Count,
            extraTailMessageCount: 0,
            tools: null,
            baseInstructionsFingerprint: null,
            toolFingerprint: null);

        try
        {
            var response = await _chatClient.GetResponseAsync(
                summaryMessages,
                new ChatOptions { Tools = null },
                cancellationToken);
            _traceCollector?.RecordMaintenanceForkResponse(
                sessionKey,
                MaintenanceForkTaskKind.ContextCompaction,
                response,
                CompactionTrace.ClassifyFallbackReason(response));
            return response?.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _traceCollector?.RecordMaintenanceForkResponse(
                sessionKey,
                MaintenanceForkTaskKind.ContextCompaction,
                ex.Message);
            throw;
        }
    }

    private static bool TryBuildSnapshotTail(
        PromptRequestSnapshot snapshot,
        IReadOnlyList<ChatMessage> messages,
        out IReadOnlyList<ChatMessage> tail)
    {
        tail = [];
        if (snapshot.Messages.Count > messages.Count)
            return false;

        for (var i = 0; i < snapshot.Messages.Count; i++)
        {
            if (!MessagesEquivalentForPrefix(snapshot.Messages[i], messages[i]))
                return false;
        }

        tail = messages
            .Skip(snapshot.Messages.Count)
            .Select(message => message.Clone())
            .ToArray();
        return true;
    }

    private static bool MessagesEquivalentForPrefix(ChatMessage left, ChatMessage right)
    {
        if (left.Role != right.Role ||
            !string.Equals(left.AuthorName, right.AuthorName, StringComparison.Ordinal) ||
            left.Contents.Count != right.Contents.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Contents.Count; i++)
        {
            if (!ContentsEquivalentForPrefix(left.Contents[i], right.Contents[i]))
                return false;
        }

        return true;
    }

    private static bool ContentsEquivalentForPrefix(AIContent left, AIContent right)
    {
        if (left.GetType() != right.GetType())
            return false;

        return left switch
        {
            TextContent l when right is TextContent r =>
                string.Equals(l.Text, r.Text, StringComparison.Ordinal),
            FunctionCallContent l when right is FunctionCallContent r =>
                string.Equals(l.CallId, r.CallId, StringComparison.Ordinal)
                && string.Equals(l.Name, r.Name, StringComparison.Ordinal),
            FunctionResultContent l when right is FunctionResultContent r =>
                string.Equals(l.CallId, r.CallId, StringComparison.Ordinal),
            DataContent l when right is DataContent r =>
                string.Equals(l.MediaType, r.MediaType, StringComparison.OrdinalIgnoreCase),
            UriContent l when right is UriContent r =>
                string.Equals(l.Uri?.ToString(), r.Uri?.ToString(), StringComparison.Ordinal)
                && string.Equals(l.MediaType, r.MediaType, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal)
        };
    }

    private static IReadOnlyList<ChatMessage> SanitizeForSummary(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            List<AIContent>? contents = null;
            for (var i = 0; i < message.Contents.Count; i++)
            {
                var content = message.Contents[i];
                if (!ShouldOmitPayload(content))
                    continue;

                contents ??= new List<AIContent>(message.Contents);
                contents[i] = new TextContent("[Large binary content omitted during context compaction.]");
            }

            if (contents is null)
            {
                result.Add(message);
                continue;
            }

            result.Add(new ChatMessage(message.Role, contents)
            {
                AuthorName = message.AuthorName,
                MessageId = message.MessageId
            });
        }

        return result;
    }

    private static bool ShouldOmitPayload(AIContent content) =>
        content switch
        {
            DataContent dc => IsBinaryMediaType(dc.MediaType),
            UriContent uc => IsBinaryMediaType(uc.MediaType),
            _ => false
        };

    private static bool IsBinaryMediaType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType)
        && (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase));

    private static string BuildFullContextCompactionTaskInstructions()
    {
        var prompt = CompactionPrompts.GetCompactPrompt();
        return $"""
{prompt}

Compaction boundary:
- Summarize the complete conversation visible above.
- The resulting summary will replace the current model-visible history.
- Return the same <analysis> then <summary> structure requested above.
""";
    }
}
