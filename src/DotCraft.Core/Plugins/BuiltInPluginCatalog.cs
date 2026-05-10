using System.Reflection;

namespace DotCraft.Plugins;

/// <summary>
/// Provides manifest metadata for built-in plugins without installing them into a workspace.
/// </summary>
public sealed class BuiltInPluginCatalog(string? cacheRoot = null)
{
    private readonly string _cacheRoot = cacheRoot ?? GetDefaultCacheRoot();

    public static string GetDefaultCacheRoot(Assembly? resourceAssembly = null)
    {
        var assembly = resourceAssembly ?? typeof(BuiltInPluginCatalog).Assembly;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".craft",
            "cache",
            "builtin-plugins",
            assembly.GetName().Version?.ToString() ?? "0.0.0.0");
    }

    public PluginDiscoveryResult Discover(Assembly? resourceAssembly = null)
    {
        var diagnostics = new List<PluginDiagnostic>();
        var assembly = resourceAssembly ?? typeof(BuiltInPluginCatalog).Assembly;
        diagnostics.AddRange(new BuiltInPluginDeployer(_cacheRoot).Deploy(assembly));

        var plugins = new List<DiscoveredPlugin>();
        if (!Directory.Exists(_cacheRoot))
            return new PluginDiscoveryResult(plugins, diagnostics);

        foreach (var pluginRoot in Directory.GetDirectories(_cacheRoot).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (!PluginManifestParser.IsValidPluginRoot(pluginRoot))
                continue;

            var parse = PluginManifestParser.Load(pluginRoot);
            diagnostics.AddRange(parse.Diagnostics);
            if (parse.Manifest == null)
                continue;

            plugins.Add(new DiscoveredPlugin(
                parse.Manifest,
                PluginDiscoverySourceKind.BuiltIn,
                _cacheRoot,
                Enabled: false,
                Installed: false,
                Installable: true,
                Removable: false));
        }

        return new PluginDiscoveryResult(plugins, diagnostics);
    }
}
