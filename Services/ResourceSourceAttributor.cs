using InstantEdit.Models;
using Dalamud.Plugin.Services;

namespace InstantEdit.Services;

/// <summary>
/// Attributes physical resolved paths to registered Penumbra mod directories. This
/// identifies only the directory containing a file; it never infers option, priority,
/// or which collection decision produced the resolved path.
/// </summary>
public sealed class ResourceSourceAttributor
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly PenumbraService _penumbra;
    private readonly IPluginLog _log;
    private readonly object _lock = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private IReadOnlyList<ModRoot> _modRoots = Array.Empty<ModRoot>();

    public ResourceSourceAttributor(PenumbraService penumbra, IPluginLog log)
    {
        _penumbra = penumbra;
        _log = log;
    }

    public ResourceSource AttributionFor(string? actualPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath))
            return new ResourceSource(ResourceSourceState.SourceUnavailable, "Source unavailable", null);

        if (!Path.IsPathRooted(actualPath))
            return new ResourceSource(ResourceSourceState.GameData, "Game data", null);

        var physicalPath = NormalizePhysicalPath(actualPath);
        if (physicalPath is null)
            return new ResourceSource(ResourceSourceState.SourceUnavailable, "Source unavailable", null);

        foreach (var mod in GetModRoots())
        {
            if (!IsPathWithin(physicalPath, mod.Path))
                continue;
            return new ResourceSource(ResourceSourceState.LoadedMod, $"Loaded from: {mod.Name}", mod.Name);
        }

        return new ResourceSource(ResourceSourceState.ExternalResolvedFile, "External resolved file", null);
    }

    private IReadOnlyList<ModRoot> GetModRoots()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)
                return _modRoots;

            _lastRefreshUtc = DateTime.UtcNow;
            _modRoots = BuildModRoots();
            return _modRoots;
        }
    }

    private IReadOnlyList<ModRoot> BuildModRoots()
    {
        try
        {
            var modDirectory = _penumbra.GetModDirectory();
            if (string.IsNullOrWhiteSpace(modDirectory))
                return Array.Empty<ModRoot>();

            var modRoot = NormalizePhysicalPath(modDirectory);
            if (modRoot is null)
                return Array.Empty<ModRoot>();

            var roots = new List<ModRoot>();
            foreach (var (directory, modName) in _penumbra.GetModList())
            {
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(modName))
                    continue;

                var path = NormalizePhysicalPath(Path.Combine(modRoot, directory));
                if (path is not null && IsPathWithin(path, modRoot))
                    roots.Add(new ModRoot(path, modName));
            }

            return roots.OrderByDescending(root => root.Path.Length).ToArray();
        }
        catch (Exception e)
        {
            _log.Debug($"Could not build Penumbra mod source map: {e.Message}");
            return Array.Empty<ModRoot>();
        }
    }

    private static string? NormalizePhysicalPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsPathWithin(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ModRoot(string Path, string Name);
}

public sealed record ResourceSource(ResourceSourceState State, string Label, string? ModName);
