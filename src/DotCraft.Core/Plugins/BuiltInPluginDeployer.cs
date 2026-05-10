using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotCraft.Plugins;

/// <summary>
/// Deploys built-in plugin manifests embedded in the DotCraft assembly.
/// </summary>
public sealed class BuiltInPluginDeployer(string workspacePluginsPath)
{
    private const string ResourcePrefix = "DotCraft.Plugins.BuiltIn.";
    private static readonly Lock DeploymentLock = new();

    public const string MarkerFile = ".builtin";

    /// <summary>
    /// Deploys embedded built-in plugins into the workspace plugin directory.
    /// </summary>
    public IReadOnlyList<PluginDiagnostic> Deploy(Assembly? resourceAssembly = null)
        => DeployCore(targetPluginId: null, resourceAssembly);

    /// <summary>
    /// Deploys one embedded built-in plugin into the workspace plugin directory.
    /// </summary>
    public IReadOnlyList<PluginDiagnostic> DeployPlugin(string pluginId, Assembly? resourceAssembly = null)
        => DeployCore(PluginIds.Canonicalize(pluginId), resourceAssembly);

    public static bool IsManagedBuiltInPluginRoot(string pluginRoot) =>
        File.Exists(Path.Combine(pluginRoot, MarkerFile));

    private IReadOnlyList<PluginDiagnostic> DeployCore(string? targetPluginId, Assembly? resourceAssembly)
    {
        lock (DeploymentLock)
        {
            return DeployCoreLocked(targetPluginId, resourceAssembly);
        }
    }

    private IReadOnlyList<PluginDiagnostic> DeployCoreLocked(string? targetPluginId, Assembly? resourceAssembly)
    {
        var diagnostics = new List<PluginDiagnostic>();
        var assembly = resourceAssembly ?? typeof(BuiltInPluginDeployer).Assembly;
        var currentVersion = assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        var groups = GetResourceGroups(assembly);
        var deployed = false;

        Directory.CreateDirectory(workspacePluginsPath);
        foreach (var group in groups)
        {
            var pluginId = ReadBuiltInPluginId(assembly, group) ?? group.Key;
            var markerText = BuildMarkerText(assembly, group, currentVersion);
            if (!string.IsNullOrWhiteSpace(targetPluginId)
                && !PluginIds.EqualsCanonical(pluginId, targetPluginId))
                continue;

            var pluginDir = Path.Combine(workspacePluginsPath, pluginId);
            var markerPath = Path.Combine(pluginDir, MarkerFile);
            if (Directory.Exists(pluginDir) && !File.Exists(markerPath))
            {
                diagnostics.Add(PluginDiagnostic.Info(
                    "BuiltInPluginUserOwned",
                    $"Built-in plugin '{pluginId}' was not deployed because the target directory is user-owned.",
                    pluginId,
                    path: pluginDir));
                continue;
            }

            if (File.Exists(markerPath)
                && string.Equals(File.ReadAllText(markerPath).Trim(), markerText, StringComparison.Ordinal))
            {
                deployed = true;
                continue;
            }

            Directory.CreateDirectory(pluginDir);
            foreach (var resource in group)
            {
                var relativePath = NormalizeBuiltInResourceFileName(resource.FileName);
                using var stream = assembly.GetManifestResourceStream(resource.ResourceName);
                if (stream == null)
                    continue;

                var targetPath = Path.Combine(pluginDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                WriteResourceAtomically(stream, targetPath);
            }

            WriteTextAtomically(markerText, markerPath);
            deployed = true;
        }

        if (!string.IsNullOrWhiteSpace(targetPluginId) && !deployed)
        {
            diagnostics.Add(PluginDiagnostic.Warning(
                "BuiltInPluginNotFound",
                $"Built-in plugin '{targetPluginId}' was not found.",
                targetPluginId));
        }

        return diagnostics;
    }

    private static void WriteResourceAtomically(Stream source, string targetPath)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(file);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void WriteTextAtomically(string text, string targetPath)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, text);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static IReadOnlyList<IGrouping<string, (string PluginId, string FileName, string ResourceName)>>
        GetResourceGroups(Assembly assembly) =>
        assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name =>
            {
                var remainder = name[ResourcePrefix.Length..];
                var dotIndex = remainder.IndexOf('.');
                if (dotIndex <= 0)
                    return (PluginId: string.Empty, FileName: string.Empty, ResourceName: name);

                return (
                    PluginId: remainder[..dotIndex],
                    FileName: remainder[(dotIndex + 1)..],
                    ResourceName: name);
            })
            .Where(resource => !string.IsNullOrWhiteSpace(resource.PluginId)
                               && !string.IsNullOrWhiteSpace(resource.FileName))
            .GroupBy(resource => resource.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeBuiltInResourceFileName(string fileName)
    {
        if (fileName.StartsWith("_craft-plugin.", StringComparison.Ordinal))
            return Path.Combine(".craft-plugin", fileName["_craft-plugin.".Length..]);
        if (fileName.StartsWith("_craft_plugin.", StringComparison.Ordinal))
            return Path.Combine(".craft-plugin", fileName["_craft_plugin.".Length..]);
        if (fileName.StartsWith(".craft-plugin.", StringComparison.Ordinal))
            return Path.Combine(".craft-plugin", fileName[".craft-plugin.".Length..]);
        if (fileName.StartsWith(".craft_plugin.", StringComparison.Ordinal))
            return Path.Combine(".craft-plugin", fileName[".craft_plugin.".Length..]);
        if (fileName.StartsWith("craft-plugin.", StringComparison.Ordinal))
            return Path.Combine(".craft-plugin", fileName["craft-plugin.".Length..]);
        if (fileName.StartsWith("craft_plugin.", StringComparison.Ordinal))
            return Path.Combine(".craft-plugin", fileName["craft_plugin.".Length..]);

        if (fileName.StartsWith("skills.", StringComparison.Ordinal))
        {
            var remainder = fileName["skills.".Length..];
            var dotIndex = remainder.IndexOf('.');
            if (dotIndex > 0)
            {
                var skillName = remainder[..dotIndex].Replace('_', '-');
                var skillFileName = remainder[(dotIndex + 1)..];
                if (skillFileName.StartsWith("agents.", StringComparison.Ordinal))
                    return Path.Combine("skills", skillName, "agents", skillFileName["agents.".Length..]);
                if (skillFileName.StartsWith("assets.", StringComparison.Ordinal))
                    return Path.Combine("skills", skillName, "assets", skillFileName["assets.".Length..]);
                return Path.Combine("skills", skillName, skillFileName);
            }
        }

        if (fileName.StartsWith("scripts.", StringComparison.Ordinal))
            return Path.Combine("scripts", fileName["scripts.".Length..]);

        if (fileName.StartsWith("extension.", StringComparison.Ordinal))
            return Path.Combine("extension", fileName["extension.".Length..]);

        if (fileName.StartsWith("native-host.", StringComparison.Ordinal))
        {
            var parts = fileName["native-host.".Length..].Split('.');
            if (parts.Length >= 4)
                return Path.Combine("native-host", parts[0], parts[1], string.Join('.', parts.Skip(2)));
        }

        return fileName;
    }

    private static string BuildMarkerText(
        Assembly assembly,
        IGrouping<string, (string PluginId, string FileName, string ResourceName)> resources,
        string version)
    {
        using var hash = SHA256.Create();
        foreach (var resource in resources.OrderBy(resource => resource.ResourceName, StringComparer.Ordinal))
        {
            AddHashBytes(hash, Encoding.UTF8.GetBytes(resource.ResourceName));
            using var stream = assembly.GetManifestResourceStream(resource.ResourceName);
            if (stream == null)
                continue;

            stream.CopyTo(new HashStream(hash));
        }

        hash.TransformFinalBlock([], 0, 0);
        var digest = Convert.ToHexString(hash.Hash ?? []);
        return $"{version};sha256:{digest}";
    }

    private static void AddHashBytes(HashAlgorithm hash, byte[] bytes)
    {
        hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
        hash.TransformBlock([0], 0, 1, null, 0);
    }

    private static string? ReadBuiltInPluginId(
        Assembly assembly,
        IEnumerable<(string PluginId, string FileName, string ResourceName)> resources)
    {
        var manifestResource = resources.FirstOrDefault(
            resource => string.Equals(
                NormalizeBuiltInResourceFileName(resource.FileName),
                Path.Combine(".craft-plugin", "plugin.json"),
                StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(manifestResource.ResourceName))
            return null;

        using var stream = assembly.GetManifestResourceStream(manifestResource.ResourceName);
        if (stream == null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class HashStream(HashAlgorithm hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.TransformBlock(buffer, offset, count, null, 0);
        }
    }
}
