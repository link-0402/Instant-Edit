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
            return new ResourceSource(ResourceSourceState.SourceUnavailable, "Source unavailable", null, null, null, null);

        if (!Path.IsPathRooted(actualPath))
            return new ResourceSource(ResourceSourceState.GameData, "Game data", null, null, null, actualPath);

        var physicalPath = NormalizePhysicalPath(actualPath);
        if (physicalPath is null)
            return new ResourceSource(ResourceSourceState.SourceUnavailable, "Source unavailable", null, null, null, null);

        foreach (var mod in GetModRoots())
        {
            if (!IsPathWithin(physicalPath, mod.Path))
                continue;
            var relativePath = Path.GetRelativePath(mod.Path, physicalPath).Replace('\\', '/');
            return new ResourceSource(
                ResourceSourceState.LoadedMod,
                $"Loaded from: {mod.Name}",
                mod.Name,
                mod.Directory,
                mod.Path,
                relativePath);
        }

        return new ResourceSource(ResourceSourceState.ExternalResolvedFile, "External resolved file", null, null, null, physicalPath);
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
            var modRoot = NormalizePhysicalPath(_penumbra.GetModDirectory());

            var roots = new List<ModRoot>();
            foreach (var (directory, modName) in _penumbra.GetModList())
            {
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(modName))
                    continue;

                // The standard Penumbra layout is just a string concatenation; it does
                // not touch the filesystem or any IPC and stays cheap even when the
                // Penumbra mod folder holds thousands of directories. Use it for every
                // mod before consulting the per-mod registered path so the on-screen and
                // search tabs still populate under that scale.
                string? path = null;
                bool fromRegistered = false;
                if (modRoot is not null)
                {
                    var standardPath = NormalizePhysicalPath(Path.Combine(modRoot, directory));
                    if (standardPath is not null && IsPathWithin(standardPath, modRoot))
                        path = standardPath;
                }

                // A mod stored outside Penumbra's global root is only discoverable via
                // its Penumbra IPC entry. The fast path above handles every standard
                // layout, so this branch fires only for genuinely relocated mods.
                if (path is null)
                {
                    var registeredPath = _penumbra.GetRegisteredModPath(directory);
                    path = NormalizePhysicalPath(registeredPath);
                    fromRegistered = registeredPath is not null;
                }

                if (path is not null && (modRoot is null ||
                    fromRegistered || IsPathWithin(path, modRoot)))
                    roots.Add(new ModRoot(path, directory, modName));
            }

            return roots.OrderByDescending(root => root.Path.Length).ToArray();
        }
        catch (Exception e)
        {
            _log.Debug($"Could not build Penumbra mod source map: {e.Message}");
            return Array.Empty<ModRoot>();
        }
    }

    private static string? NormalizePhysicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
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

    private sealed record ModRoot(string Path, string Directory, string Name);
}

public sealed record ResourceSource(
    ResourceSourceState State,
    string Label,
    string? ModName,
    string? ModDirectory,
    string? ModRootPath,
    string? RelativePath);
