namespace DotCraft.Protocol;

/// <summary>
/// Accumulated token usage for a Turn.
/// </summary>
public sealed record TokenUsageInfo
{
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CachedInputTokens { get; init; }

    public long NonCachedInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    public long ReasoningOutputTokens { get; init; }

    public double CacheHitRate => InputTokens > 0
        ? CachedInputTokens / (double)InputTokens
        : 0;

    public long TotalTokens { get; init; }

    public static TokenUsageInfo operator +(TokenUsageInfo a, TokenUsageInfo b) =>
        new()
        {
            InputTokens = a.InputTokens + b.InputTokens,
            OutputTokens = a.OutputTokens + b.OutputTokens,
            CachedInputTokens = a.CachedInputTokens + b.CachedInputTokens,
            ReasoningOutputTokens = a.ReasoningOutputTokens + b.ReasoningOutputTokens,
            TotalTokens = a.TotalTokens + b.TotalTokens
        };
}
