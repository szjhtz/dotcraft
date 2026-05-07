using DotCraft.Abstractions;
using DotCraft.Agents;
using DotCraft.Configuration;
using DotCraft.Memory;
using DotCraft.Security;
using DotCraft.Skills;
using Microsoft.Extensions.AI;

namespace DotCraft.Tests.Agents;

public sealed class AgentFactoryModeToolSchemaTests : IDisposable
{
    private readonly string _tempDir;

    public AgentFactoryModeToolSchemaTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ModeToolSchema_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task CreateToolsForMode_ReturnsSameToolNamesForPlanAndAgent()
    {
        await using var factory = CreateFactory();

        var planTools = factory.CreateToolsForMode(AgentMode.Plan).Select(t => t.Name).ToArray();
        var agentTools = factory.CreateToolsForMode(AgentMode.Agent).Select(t => t.Name).ToArray();

        Assert.Equal(agentTools, planTools);
        Assert.Contains("WriteFile", planTools);
        Assert.Contains("EditFile", planTools);
        Assert.Contains("Exec", planTools);
        Assert.Contains("CreatePlan", planTools);
        Assert.Contains("UpdateTodos", planTools);
        Assert.Contains("TodoWrite", planTools);
    }

    private AgentFactory CreateFactory()
    {
        var config = new AppConfig
        {
            ApiKey = "sk-test-not-used-for-network",
            EndPoint = "https://127.0.0.1:9/v1"
        };
        return new AgentFactory(
            dotcraftPath: _tempDir,
            workspacePath: _tempDir,
            config: config,
            memoryStore: new MemoryStore(_tempDir),
            skillsLoader: new SkillsLoader(_tempDir),
            approvalService: new AutoApproveApprovalService(),
            blacklist: null,
            toolProviders: [new FakeCoreToolProvider()],
            planStore: new PlanStore(_tempDir));
    }

    private sealed class FakeCoreToolProvider : IAgentToolProvider
    {
        public IEnumerable<AITool> CreateTools(ToolProviderContext context)
        {
            yield return AIFunctionFactory.Create(() => "read", name: "ReadFile");
            yield return AIFunctionFactory.Create(() => "wrote", name: "WriteFile");
            yield return AIFunctionFactory.Create(() => "edited", name: "EditFile");
            yield return AIFunctionFactory.Create(() => "output", name: "Exec");
        }
    }
}
