using DotCraft.Context.Compaction;

namespace DotCraft.Tests.Context.Compaction;

public sealed class CompactionErrorsTests
{
    [Fact]
    public void IsPromptTooLong_WalksInnerExceptions()
    {
        var exception = new InvalidOperationException(
            "outer wrapper",
            new InvalidOperationException("context_length_exceeded"));

        Assert.True(CompactionErrors.IsPromptTooLong(exception));
    }

    [Fact]
    public void IsPromptTooLong_ReturnsFalseWhenNoKnownMarkerExists()
    {
        var exception = new InvalidOperationException(
            "outer wrapper",
            new InvalidOperationException("provider unavailable"));

        Assert.False(CompactionErrors.IsPromptTooLong(exception));
    }
}
