using DotCraft.Protocol;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.AppServer;

public sealed class SessionWireInputPartTests
{
    [Fact]
    public void ToAIContent_LeavesAttachedFileMarkerTextUnchanged()
    {
        var part = new SessionWireInputPart
        {
            Type = "text",
            Text = "[[Attached File: C:\\logs\\a.txt]]\n[[Attached File: D:\\docs\\b.md]]\n\nReview these"
        };

        var content = Assert.IsType<TextContent>(part.ToAIContent());

        Assert.Equal("[[Attached File: C:\\logs\\a.txt]]\n[[Attached File: D:\\docs\\b.md]]\n\nReview these", content.Text);
    }
}

