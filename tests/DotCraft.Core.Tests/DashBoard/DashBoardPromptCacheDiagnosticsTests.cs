using DotCraft.DashBoard;

namespace DotCraft.Tests.DashBoard;

public sealed class DashBoardPromptCacheDiagnosticsTests
{
    [Fact]
    public void DashboardHtml_IncludesPromptCacheDiagnosticDecorationContract()
    {
        var html = DashBoardFrontend.GetHtml();

        Assert.Contains("function decorateTraceEventsWithPromptCacheDiagnostics(events)", html);
        Assert.Contains("event.promptCacheEventKind !== 'baseline'", html);
        Assert.Contains("event.type !== 'SessionMetadata'", html);
        Assert.Contains("event.type === 'ToolCallStarted' || event.type === 'Thinking' || event.type === 'Response'", html);
        Assert.Contains("case 'PromptCacheDiagnostic'", html);
        Assert.Contains("Prompt cache drift: system prompt changed", html);
        Assert.Contains("Prompt cache drift: tool schema changed", html);
        Assert.Contains("Tool schema extended", html);
        Assert.Contains("badge badge-info\">tool extension", html);
        Assert.Contains("renderTraceTimeline(visibleEvents)", html);
    }
}
