using DotCraft.AppServer;
using DotCraft.Hub;

namespace DotCraft.Tests.Hub;

public sealed class ManagedAppServerRegistryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "DotCraftManagedRegistry_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetApiProxyRestartReason_ReturnsNullWhenRunningProxyMatchesRequest()
    {
        var binary = Touch("cliproxyapi.exe");
        var config = Touch("proxy.yaml");
        var request = new HubApiProxySidecarRequest
        {
            Enabled = true,
            BinaryPath = binary,
            ConfigPath = config,
            Endpoint = "http://127.0.0.1:8317/v1",
            ApiKey = "proxy-key"
        };

        var reason = ManagedAppServerRegistry.GetApiProxyRestartReason(
            request,
            currentEndpoint: "http://127.0.0.1:8317/v1",
            currentBinaryPath: binary,
            currentConfigPath: config,
            currentApiKey: "proxy-key");

        Assert.Null(reason);
    }

    [Fact]
    public void GetApiProxyRestartReason_ReportsChangedProxyIdentityForReconnectDiagnostics()
    {
        var binary = Touch("cliproxyapi.exe");
        var config = Touch("proxy.yaml");
        var request = new HubApiProxySidecarRequest
        {
            Enabled = true,
            BinaryPath = binary,
            ConfigPath = config,
            Endpoint = "http://127.0.0.1:8317/v1",
            ApiKey = "proxy-key"
        };

        var endpointReason = ManagedAppServerRegistry.GetApiProxyRestartReason(
            request,
            currentEndpoint: "http://127.0.0.1:8318/v1",
            currentBinaryPath: binary,
            currentConfigPath: config,
            currentApiKey: "proxy-key");
        var keyReason = ManagedAppServerRegistry.GetApiProxyRestartReason(
            request,
            currentEndpoint: "http://127.0.0.1:8317/v1",
            currentBinaryPath: binary,
            currentConfigPath: config,
            currentApiKey: "old-proxy-key");
        var disabledReason = ManagedAppServerRegistry.GetApiProxyRestartReason(
            new HubApiProxySidecarRequest { Enabled = false },
            currentEndpoint: "http://127.0.0.1:8317/v1",
            currentBinaryPath: binary,
            currentConfigPath: config,
            currentApiKey: "proxy-key");

        Assert.Contains("endpoint changed", endpointReason);
        Assert.Contains("API key changed", keyReason);
        Assert.Contains("disabled", disabledReason);
    }

    [Fact]
    public void CleanupStaleFiles_RemovesGuardLeftByKilledManagedAppServer()
    {
        var craftPath = Path.Combine(_tempDir, ".craft");
        Directory.CreateDirectory(craftPath);
        var lockPath = AppServerWorkspaceLock.GetLockFilePath(craftPath);
        var json = System.Text.Json.JsonSerializer.Serialize(new AppServerLockInfo(
            Pid: 999999,
            WorkspacePath: _tempDir,
            ManagedByHub: true,
            HubApiBaseUrl: "http://127.0.0.1:43000",
            StartedAt: DateTimeOffset.UtcNow,
            Version: "test",
            Endpoints: new Dictionary<string, string>()), HubJson.Options);
        File.WriteAllText(lockPath, json);
        File.WriteAllText(lockPath + ".guard", string.Empty);

        AppServerWorkspaceLock.CleanupStaleFiles(craftPath);

        Assert.False(File.Exists(lockPath));
        Assert.False(File.Exists(lockPath + ".guard"));
    }

    [Fact]
    public void AddRuntimeTools_ForwardsRipgrepPathAsEnvironmentOverride()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        ManagedAppServerRegistry.AddRuntimeTools(
            new HubRuntimeToolsRequest { RipgrepPath = " C:/Tools/rg.exe " },
            env);

        Assert.Equal("C:/Tools/rg.exe", env["DOTCRAFT_RG_PATH"]);
    }

    private string Touch(string fileName)
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // ignored
        }
    }
}
