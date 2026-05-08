using Microsoft.Extensions.AI;

namespace DotCraft.Context.Compaction;

/// <summary>
/// Result of a partial compaction run.
/// </summary>
public sealed record PartialCompactResult(
    IReadOnlyList<ChatMessage> SummarizedPrefix,
    IReadOnlyList<ChatMessage> PreservedTail,
    string FormattedSummary,
    string RawSummary,
    int PrefixEstimatedTokens,
    int TailEstimatedTokens);

/// <summary>
/// Result envelope for a partial compaction attempt, including a machine-readable
/// reason when no summary can be produced.
/// </summary>
public sealed record PartialCompactAttempt(PartialCompactResult? Result, string? Reason)
{
    public static PartialCompactAttempt Succeeded(PartialCompactResult result) => new(result, null);

    public static PartialCompactAttempt Unavailable(string reason) => new(null, reason);
}

/// <summary>
/// Summarizes the older portion of a conversation while preserving a tail of
/// recent API-round groups verbatim. Port of openclaude's
/// <c>sessionMemoryCompact.ts</c> / <c>calculateMessagesToKeepIndex</c>.
/// </summary>
public sealed class PartialCompactor
{
    private readonly IChatClient _chatClient;
    private readonly CompactionConfig _config;
    private readonly MaintenanceForkRunner? _maintenanceForkRunner;

    public PartialCompactor(
        IChatClient chatClient,
        CompactionConfig config,
        MaintenanceForkRunner? maintenanceForkRunner = null)
    {
        _chatClient = chatClient;
        _config = config;
        _maintenanceForkRunner = maintenanceForkRunner;
    }

    /// <summary>
    /// Picks the split point in <paramref name="messages"/> that preserves
    /// <paramref name="config"/>.KeepRecent*... as the tail, returning the
    /// index of the first preserved message. Visible for tests.
    /// </summary>
    public static int CalculateSplitIndex(
        IReadOnlyList<ChatMessage> messages,
        CompactionConfig config)
    {
        if (messages.Count == 0)
            return 0;

        var groups = MessageGrouper.GroupByApiRound(messages);
        if (groups.Count == 0)
            return 0;

        long tailTokens = 0;
        int tailGroups = 0;
        int splitGroupIndex = groups.Count;

        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var group = groups[i];
            var nextTokens = tailTokens + group.EstimatedTokens;
            if (nextTokens > config.KeepRecentMaxTokens && tailGroups > 0)
                break;

            tailTokens = nextTokens;
            tailGroups++;
            splitGroupIndex = i;

            if (tailTokens >= config.KeepRecentMinTokens && tailGroups >= config.KeepRecentMinGroups)
                break;
        }

        // All groups may be tail; in that case there's nothing to summarize.
        if (splitGroupIndex == 0)
            return 0;

        var splitMessageIndex = 0;
        for (var i = 0; i < splitGroupIndex; i++)
            splitMessageIndex += groups[i].Messages.Count;

        return splitMessageIndex;
    }

    /// <summary>
    /// Runs the partial summary and returns either a summary result or a
    /// machine-readable reason explaining why no summary can be produced. The
    /// caller is responsible for replacing the prefix in the session's chat
    /// history when the attempt succeeds.
    /// </summary>
    public async Task<PartialCompactAttempt> CompactAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        return await CompactAsync(messages, snapshot: null, cancellationToken);
    }

    /// <summary>
    /// Runs partial summary using a captured request snapshot when available.
    /// The snapshot path preserves the main request prefix and appends only a
    /// maintenance task at the tail.
    /// </summary>
    public async Task<PartialCompactAttempt> CompactAsync(
        IReadOnlyList<ChatMessage> messages,
        PromptRequestSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return PartialCompactAttempt.Unavailable("empty_history");

        var splitIndex = CalculateSplitIndex(messages, _config);
        if (splitIndex <= 0)
            return PartialCompactAttempt.Unavailable("no_summarizable_prefix");

        var prefix = messages.Take(splitIndex).ToList();
        var tail = messages.Skip(splitIndex).ToList();

        if (prefix.Count == 0)
            return PartialCompactAttempt.Unavailable("no_summarizable_prefix");

        var paired = MessageGrouper.EnsurePairing(prefix);
        if (paired.Count == 0)
            return PartialCompactAttempt.Unavailable("no_summarizable_prefix");

        var prefixTokens = MessageTokenEstimator.Estimate(prefix);
        var tailTokens = MessageTokenEstimator.Estimate(tail);

        var rawSummary = snapshot is not null && _maintenanceForkRunner is not null
            ? await RunSnapshotForkAsync(snapshot, splitIndex, tail, cancellationToken)
            : await RunLegacySummaryAsync(paired, cancellationToken);
        if (string.IsNullOrWhiteSpace(rawSummary))
            return PartialCompactAttempt.Unavailable("summary_unavailable");

        var formatted = CompactionPrompts.GetCompactUserSummaryMessage(
            rawSummary,
            transcriptPath: null,
            recentMessagesPreserved: tail.Count > 0);

        return PartialCompactAttempt.Succeeded(new PartialCompactResult(
            SummarizedPrefix: prefix,
            PreservedTail: tail,
            FormattedSummary: formatted,
            RawSummary: rawSummary,
            PrefixEstimatedTokens: prefixTokens,
            TailEstimatedTokens: tailTokens));
    }

    private async Task<string?> RunSnapshotForkAsync(
        PromptRequestSnapshot snapshot,
        int splitIndex,
        IReadOnlyList<ChatMessage> tail,
        CancellationToken cancellationToken)
    {
        var instructions = BuildContextCompactionTaskInstructions(splitIndex, tail);
        var result = await _maintenanceForkRunner!.RunAsync(
            snapshot,
            new MaintenanceForkTask(
                MaintenanceForkTaskKind.ContextCompaction,
                instructions),
            cancellationToken);

        return result.FallbackReason is null ? result.Text : null;
    }

    private async Task<string?> RunLegacySummaryAsync(
        IReadOnlyList<ChatMessage> paired,
        CancellationToken cancellationToken)
    {
        var summaryPrompt = CompactionPrompts.GetPartialCompactPrompt();
        var summaryMessages = new List<ChatMessage>(paired.Count + 1)
        {
            new(ChatRole.System, summaryPrompt)
        };
        summaryMessages.AddRange(paired);

        try
        {
            var response = await _chatClient.GetResponseAsync(
                summaryMessages,
                new ChatOptions { Tools = null },
                cancellationToken);
            return response?.Text;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildContextCompactionTaskInstructions(
        int splitIndex,
        IReadOnlyList<ChatMessage> tail)
    {
        var prompt = CompactionPrompts.GetPartialCompactPrompt();
        var firstTail = tail.FirstOrDefault();
        var tailHint = firstTail is null
            ? "No recent tail messages are preserved."
            : $"The preserved recent tail starts at message index {splitIndex} with role '{firstTail.Role}'. Do not summarize the preserved tail as completed work; it will remain verbatim after the summary.";

        return $"""
{prompt}

Compaction boundary:
- Summarize the conversation before message index {splitIndex}.
- {tailHint}
- Return the same <analysis> then <summary> structure requested above.
""";
    }
}
