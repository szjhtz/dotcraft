using System.Text.Json;
using DotCraft.Context.Compaction;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Context.Compaction;

public sealed class MessageTokenEstimatorTests
{
    [Fact]
    public void EstimateContent_Text()
    {
        var content = new TextContent("hello world"); // 11 UTF-8 bytes -> ceil(11/4) = 3
        Assert.Equal(3, MessageTokenEstimator.EstimateContent(content));
    }

    [Fact]
    public void EstimateContent_CjkTextUsesUtf8Bytes()
    {
        var content = new TextContent("你好"); // 6 UTF-8 bytes -> ceil(6/4) = 2
        Assert.Equal(2, MessageTokenEstimator.EstimateContent(content));
    }

    [Fact]
    public void EstimateContent_ImageUsesFixedCost()
    {
        var content = new DataContent(new byte[] { 1, 2, 3 }, "image/png");
        Assert.Equal(2000, MessageTokenEstimator.EstimateContent(content));
    }

    [Fact]
    public void EstimateModelVisibleBytes_ReplacesLargeMediaPayload()
    {
        var media = new DataContent(new byte[1_000_000], "image/png");
        var message = new ChatMessage(ChatRole.User, [media]);

        var bytes = MessageTokenEstimator.EstimateModelVisibleBytes(message);

        Assert.InRange(bytes, 8_000, 20_000);
    }

    [Fact]
    public void EstimateContent_FunctionCallIncludesNameAndArgs()
    {
        var args = new Dictionary<string, object?> { ["path"] = "README.md" };
        var call = new FunctionCallContent("call-1", "ReadFile", args);
        var tokens = MessageTokenEstimator.EstimateContent(call);
        Assert.True(tokens > MessageTokenEstimator.RoughTokenCount("ReadFile" + JsonSerializer.Serialize(args)));
    }

    [Fact]
    public void EstimateContent_FunctionResultUsesSerializedPayload()
    {
        var fr = new FunctionResultContent("call-1", "file contents go here");
        Assert.True(MessageTokenEstimator.EstimateContent(fr) > 0);
    }

    [Fact]
    public void EstimateContent_FunctionResultIncludesDenseStructuredPayload()
    {
        var payload = new Dictionary<string, object?>
        {
            ["items"] = Enumerable.Range(0, 50).Select(i => new Dictionary<string, object?>
            {
                ["id"] = i,
                ["ok"] = true,
                ["path"] = $"src/file-{i}.cs",
            }).ToArray(),
        };
        var result = new FunctionResultContent("call-1", payload);

        Assert.True(
            MessageTokenEstimator.EstimateContent(result)
            > MessageTokenEstimator.RoughTokenCount(JsonSerializer.Serialize(payload)));
    }

    [Fact]
    public void Estimate_AppliesSafetyPad()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "hello world"),
        };
        Assert.True(MessageTokenEstimator.Estimate(messages) > MessageTokenEstimator.EstimateDelta(messages));
    }
}
