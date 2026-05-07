using DotCraft.Agents;
using DotCraft.Configuration;

namespace DotCraft.Tests.Agents;

public sealed class OpenAIClientProviderTests
{
    [Fact]
    public void ResolveSubAgentModel_ExplicitSubAgentModelWins()
    {
        var config = new AppConfig
        {
            Model = "main-model",
            SubAgent = new AppConfig.SubAgentConfig
            {
                Model = "sub-model"
            }
        };
        var provider = new OpenAIClientProvider();

        var effective = provider.ResolveSubAgentModel(config, "thread-model");

        Assert.Equal("sub-model", effective);
    }

    [Fact]
    public void ResolveSubAgentModel_EmptySubAgentModelFollowsThreadModel()
    {
        var config = new AppConfig
        {
            Model = "main-model",
            SubAgent = new AppConfig.SubAgentConfig()
        };
        var provider = new OpenAIClientProvider();

        var main = provider.ResolveMainModel(config, "thread-model");
        var subAgent = provider.ResolveSubAgentModel(config, main);

        Assert.Equal("thread-model", subAgent);
    }

    [Fact]
    public void ResolveMainModel_EmptyThreadModelFallsBackToWorkspaceModel()
    {
        var config = new AppConfig { Model = "workspace-model" };
        var provider = new OpenAIClientProvider();

        var main = provider.ResolveMainModel(config, " ");

        Assert.Equal("workspace-model", main);
    }

    [Fact]
    public void ResolveConsolidationModel_EmptyConsolidationModelFallsBackToWorkspaceModel()
    {
        var config = new AppConfig
        {
            Model = "workspace-model",
            ConsolidationModel = ""
        };
        var provider = new OpenAIClientProvider();

        var consolidation = provider.ResolveConsolidationModel(config);

        Assert.Equal("workspace-model", consolidation);
    }

    [Fact]
    public void AppConfig_DefaultNetworkTimeoutSeconds_IsTenMinutes()
    {
        var config = new AppConfig();

        Assert.Equal(600, config.NetworkTimeoutSeconds);
    }

    [Fact]
    public void CreateClientOptions_UsesConfiguredNetworkTimeout()
    {
        var endpoint = new Uri("https://example.test/v1");

        var options = OpenAIClientProvider.CreateClientOptions(endpoint, 240);

        Assert.Equal(endpoint, options.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(240), options.NetworkTimeout);
    }

    [Fact]
    public void GetOpenAIClient_CacheKeyIncludesNetworkTimeout()
    {
        var provider = new OpenAIClientProvider();
        var baseConfig = new AppConfig
        {
            ApiKey = "sk-test",
            EndPoint = "https://example.test/v1",
            NetworkTimeoutSeconds = 600
        };
        var sameConfig = new AppConfig
        {
            ApiKey = "sk-test",
            EndPoint = "https://example.test/v1",
            NetworkTimeoutSeconds = 600
        };
        var differentTimeoutConfig = new AppConfig
        {
            ApiKey = "sk-test",
            EndPoint = "https://example.test/v1",
            NetworkTimeoutSeconds = 900
        };

        var first = provider.GetOpenAIClient(baseConfig);
        var same = provider.GetOpenAIClient(sameConfig);
        var differentTimeout = provider.GetOpenAIClient(differentTimeoutConfig);

        Assert.Same(first, same);
        Assert.NotSame(first, differentTimeout);
    }
}
