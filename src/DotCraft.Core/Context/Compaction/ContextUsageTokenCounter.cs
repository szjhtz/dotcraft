using Microsoft.Extensions.AI;

namespace DotCraft.Context.Compaction;

/// <summary>
/// Captures the provider-reported context usage at a known request boundary.
/// </summary>
public sealed record ContextUsageAnchor(
    long Tokens,
    int MessageCount);

/// <summary>
/// Counts current context pressure from the last provider usage plus an
/// estimate for messages appended after that usage-bearing request.
/// </summary>
public static class ContextUsageTokenCounter
{
    /// <summary>
    /// Returns the anchored token count, or <c>null</c> when the anchor no
    /// longer lines up with the supplied history.
    /// </summary>
    public static long? EstimateFromAnchor(
        ContextUsageAnchor? anchor,
        IReadOnlyList<ChatMessage> modelVisibleHistory)
    {
        if (anchor is null)
            return null;

        if (anchor.MessageCount < 0 || anchor.MessageCount > modelVisibleHistory.Count)
            return null;

        var deltaMessages = modelVisibleHistory
            .Skip(anchor.MessageCount)
            .ToList();
        var deltaTokens = MessageTokenEstimator.Estimate(deltaMessages);
        return Math.Max(0, anchor.Tokens + deltaTokens);
    }
}
