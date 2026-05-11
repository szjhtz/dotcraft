using DotCraft.Context.Compaction;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Context.Compaction;

public sealed class ContextUsageTokenCounterTests
{
    [Fact]
    public void EstimateFromAnchor_AddsOnlyMessagesAfterUsageBoundary()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "first user"),
            new(ChatRole.Assistant, "first assistant"),
            new(ChatRole.User, new string('u', 400))
        };
        var anchor = new ContextUsageAnchor(
            Tokens: 190_000,
            MessageCount: 2);

        var tokens = ContextUsageTokenCounter.EstimateFromAnchor(anchor, messages);

        Assert.NotNull(tokens);
        Assert.True(tokens > 190_000);
        Assert.Equal(
            190_000 + MessageTokenEstimator.EstimateDelta([messages[2]]),
            tokens);
    }

    [Fact]
    public void EstimateFromAnchor_ValidatesPrefixFingerprint()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "first user"),
            new(ChatRole.Assistant, "first assistant"),
            new(ChatRole.User, "delta")
        };
        var anchor = new ContextUsageAnchor(
            Tokens: 190_000,
            MessageCount: 2,
            PrefixFingerprint: MessageTokenEstimator.ComputePrefixFingerprint(messages, 2));

        Assert.NotNull(ContextUsageTokenCounter.EstimateFromAnchor(anchor, messages));

        messages[0] = new ChatMessage(ChatRole.User, "changed first user");
        Assert.Null(ContextUsageTokenCounter.EstimateFromAnchor(anchor, messages));
    }

    [Fact]
    public void ContextTokenUsageEstimator_UsesPersistedAnchorBeforeRawPersistedTokens()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "first user"),
            new(ChatRole.Assistant, "first assistant"),
            new(ChatRole.User, new string('d', 400))
        };
        var anchor = new ContextUsageAnchor(
            Tokens: 10_000,
            MessageCount: 2,
            PrefixFingerprint: MessageTokenEstimator.ComputePrefixFingerprint(messages, 2));

        var estimate = ContextTokenUsageEstimator.Estimate(
            messages,
            memoryAnchor: null,
            persistedAnchor: anchor,
            latestProviderTokens: 0,
            persistedTokens: 190_000);

        Assert.Equal("persisted_anchor", estimate.Source);
        Assert.Equal(10_000 + MessageTokenEstimator.EstimateDelta([messages[2]]), estimate.Tokens);
    }

    [Fact]
    public void EstimateFromAnchor_ReturnsNull_WhenBoundaryNoLongerMatchesHistory()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "short")
        };
        var anchor = new ContextUsageAnchor(
            Tokens: 190_000,
            MessageCount: 2);

        Assert.Null(ContextUsageTokenCounter.EstimateFromAnchor(anchor, messages));
    }
}
