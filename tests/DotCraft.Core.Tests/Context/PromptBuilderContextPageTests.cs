using DotCraft.Context;
using DotCraft.Memory;
using DotCraft.Skills;

namespace DotCraft.Tests.Context;

public sealed class PromptBuilderContextPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PromptPages_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _workspace;
    private readonly string _craft;

    public PromptBuilderContextPageTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _craft = Path.Combine(_workspace, ".craft");
        Directory.CreateDirectory(_craft);
    }

    [Fact]
    public void BuildSystemPrompt_KeepsMemoryStableUntilRelease()
    {
        var manager = new ContextPageManager();
        var memoryStore = new MemoryStore(_craft);
        var builder = CreateBuilder(memoryStore, manager);

        memoryStore.WriteLongTerm("memory-v1");

        var first = builder.BuildSystemPrompt("thread-a");
        Assert.Contains("memory-v1", first, StringComparison.Ordinal);

        memoryStore.WriteLongTerm("memory-v2");
        manager.MarkDirty(ContextPageKeys.MemoryLongTerm("*"));

        var pinned = builder.BuildSystemPrompt("thread-a");
        Assert.Contains("memory-v1", pinned, StringComparison.Ordinal);
        Assert.DoesNotContain("memory-v2", pinned, StringComparison.Ordinal);

        var newThread = builder.BuildSystemPrompt("thread-b");
        Assert.Contains("memory-v2", newThread, StringComparison.Ordinal);

        manager.ReleaseStablePages("thread-a");
        var refreshed = builder.BuildSystemPrompt("thread-a");
        Assert.Contains("memory-v2", refreshed, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSystemPrompt_KeepsAlwaysSkillAndSummaryStableUntilRelease()
    {
        var manager = new ContextPageManager();
        var memoryStore = new MemoryStore(_craft);
        var skillsLoader = new SkillsLoader(_craft);
        WriteAlwaysSkill("description-v1", "body-v1");
        var builder = CreateBuilder(memoryStore, manager, skillsLoader);

        var first = builder.BuildSystemPrompt("thread-a");
        Assert.Contains("description-v1", first, StringComparison.Ordinal);
        Assert.Contains("body-v1", first, StringComparison.Ordinal);

        WriteAlwaysSkill("description-v2", "body-v2");
        manager.MarkDirty(ContextPageKeys.SkillsWildcard());

        var pinned = builder.BuildSystemPrompt("thread-a");
        Assert.Contains("description-v1", pinned, StringComparison.Ordinal);
        Assert.Contains("body-v1", pinned, StringComparison.Ordinal);
        Assert.DoesNotContain("description-v2", pinned, StringComparison.Ordinal);
        Assert.DoesNotContain("body-v2", pinned, StringComparison.Ordinal);

        var newThread = builder.BuildSystemPrompt("thread-b");
        Assert.Contains("description-v2", newThread, StringComparison.Ordinal);
        Assert.Contains("body-v2", newThread, StringComparison.Ordinal);

        manager.ReleaseStablePages("thread-a");
        var refreshed = builder.BuildSystemPrompt("thread-a");
        Assert.Contains("description-v2", refreshed, StringComparison.Ordinal);
        Assert.Contains("body-v2", refreshed, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSystemPrompt_WithoutContextPageManager_ReloadsImmediately()
    {
        var memoryStore = new MemoryStore(_craft);
        var builder = CreateBuilder(memoryStore, contextPageManager: null);

        memoryStore.WriteLongTerm("memory-v1");
        Assert.Contains("memory-v1", builder.BuildSystemPrompt("thread"), StringComparison.Ordinal);

        memoryStore.WriteLongTerm("memory-v2");
        var refreshed = builder.BuildSystemPrompt("thread");

        Assert.Contains("memory-v2", refreshed, StringComparison.Ordinal);
        Assert.DoesNotContain("memory-v1", refreshed, StringComparison.Ordinal);
    }

    private PromptBuilder CreateBuilder(
        MemoryStore memoryStore,
        IContextPageManager? contextPageManager,
        SkillsLoader? skillsLoader = null) =>
        new(
            memoryStore,
            skillsLoader ?? new SkillsLoader(_craft),
            _craft,
            _workspace,
            toolNamesProvider: () => [],
            contextPageManager: contextPageManager);

    private void WriteAlwaysSkill(string description, string body)
    {
        var skillDir = Path.Combine(_craft, "skills", "always-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            $"""
            ---
            name: always-skill
            description: "{description}"
            always: true
            ---

            {body}
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
