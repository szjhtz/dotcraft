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
            190_000 + MessageTokenEstimator.Estimate([messages[2]]),
            tokens);
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
