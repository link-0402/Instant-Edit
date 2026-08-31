using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Structs;
using InstantEdit.Models;

namespace InstantEdit.Services;

public sealed record MaterialResourceCandidate(
    string GamePath,
    string ActualPath,
    string? SourceModDirectory = null,
    string? SourceModRootPath = null,
    string? SourceRelativePath = null,
    IReadOnlyList<string>? OptionMemberships = null,
    string? OptionLabel = null);

public sealed record MaterialPreviewBundleResult(string? ManifestPath, IReadOnlyList<string> Warnings)
{
    public string WarningSummary
        => Warnings.Count == 0 ? string.Empty : string.Join("; ", Warnings.Take(3)) + (Warnings.Count > 3 ? $" (+{Warnings.Count - 3} more)" : string.Empty);
}

public sealed record MaterialImportBundleResult(
    ResourceDependencyManifest? ResourceManifest,
    MaterialPreviewBundleResult? Preview,
    IReadOnlyList<string> DependencyWarnings);

/// <summary>
/// Builds a display-only material package from the exact resources resolved by Penumbra.
/// The HTTP bridge transports only the manifest path; all bundle paths are relative and
/// validated again by the Blender add-on.
/// </summary>
public sealed class MaterialPreviewBundleBuilder
{
    public const string Schema = "instant-edit.material-preview";
    public const int Version = 1;

    private const int MaxMaterials = 256;
    private const int MaxTextures = 1024;
    private const int MaxDimension = 8192;
    private const long MaxDecodedBytes = 512L * 1024 * 1024;
    private const long MaxMaterialBytes = 16L * 1024 * 1024;
    private const long MaxTextureBytes = 512L * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private static readonly Regex StandardBodyMaterial = new(
        @"^mt_c\d{4}b\d{4}_(?:a|b|bibo|body|skin)\.mtrl$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RacialMaterialName = new(
        @"^mt_c\d{4}(?<identity>.+\.mtrl)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly ResourceSourceAttributor? _sourceAttributor;

    public MaterialPreviewBundleBuilder(
        IDataManager data,
        IPluginLog log,
        ResourceSourceAttributor? sourceAttributor = null)
    {
        _data = data;
        _log = log;
        _sourceAttributor = sourceAttributor;
    }

    public async Task<MaterialImportBundleResult> BuildImportBundleAsync(
        byte[] modelBytes,
        string modelGamePath,
        IReadOnlyCollection<MaterialResourceCandidate> candidates,
        string bundleDirectory,
        bool buildPreview,
        bool excludeBodyAndGeneralMaterials,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? preferredOptionMemberships = null)
    {
        var dependencyWarnings = new List<string>();
        var scopedCandidates = ScopeResourceCandidates(
            candidates, preferredOptionMemberships, dependencyWarnings);
        var resourceCache = new Dictionary<string, ResolvedResource?>(StringComparer.OrdinalIgnoreCase);
        var dependencies = await BuildDependencyManifestAsync(
            modelBytes, modelGamePath, scopedCandidates, resourceCache,
            dependencyWarnings, cancellationToken).ConfigureAwait(false);
        var preview = buildPreview
            ? await BuildAsync(
                modelBytes, modelGamePath, scopedCandidates, bundleDirectory,
                excludeBodyAndGeneralMaterials, resourceCache, cancellationToken).ConfigureAwait(false)
            : null;
        return new MaterialImportBundleResult(dependencies, preview, dependencyWarnings);
    }

    private async Task<ResourceDependencyManifest?> BuildDependencyManifestAsync(
        byte[] modelBytes,
        string modelGamePath,
        IReadOnlyCollection<MaterialResourceCandidate> candidates,
        Dictionary<string, ResolvedResource?> resourceCache,
        List<string> warnings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resources = BuildResourceMap(candidates, warnings);
            var materialNames = ReadModelMaterials(modelBytes)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxMaterials + 1)
                .ToArray();
            if (materialNames.Length is 0 or > MaxMaterials)
                return null;

            var materials = new List<MaterialDependency>(materialNames.Length);
            foreach (var modelMaterial in materialNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var materialPath = ResolveMaterialPath(modelGamePath, modelMaterial, resources, warnings);
                if (materialPath is null)
                {
                    _log.Warning("Mashup capture could not resolve model material {Material} for {ModelPath}.",
                        modelMaterial, modelGamePath);
                    return null;
                }
                var resolvedMaterial = await ResolveResourceAsync(
                    materialPath, resources, MaxMaterialBytes, resourceCache, cancellationToken).ConfigureAwait(false);
                if (resolvedMaterial is null)
                {
                    warnings.Add($"Could not read material dependency: {materialPath}");
                    return null;
                }
                var mtrl = LooseLuminaFile.Load<MtrlFile>(resolvedMaterial.Value.Bytes);
                var materialLocator = CreateLocator(materialPath, resolvedMaterial.Value, candidates);
                if (materialLocator is null)
                {
                    warnings.Add($"Could not preserve the source location for material: {materialPath}");
                    return null;
                }

                var textures = new List<TextureDependency>();
                foreach (var textureOffset in mtrl.TextureOffsets)
                {
                    var storedPath = NormaliseGamePath(ReadString(mtrl.Strings, textureOffset.Offset));
                    if (!IsSafeGameResourcePath(storedPath, ".tex"))
                    {
                        warnings.Add($"Material contains an unsafe texture path: {storedPath}");
                        return null;
                    }
                    var effectivePath = Dx11TexturePath(storedPath, textureOffset.Flags);
                    var resourceGamePath = effectivePath;
                    var resolvedTexture = await ResolveResourceAsync(
                        effectivePath, resources, MaxTextureBytes, resourceCache, cancellationToken).ConfigureAwait(false);
                    if (resolvedTexture is null &&
                        !effectivePath.Equals(storedPath, StringComparison.OrdinalIgnoreCase) &&
                        !resources.ContainsKey(effectivePath))
                    {
                        resolvedTexture = await ResolveResourceAsync(
                            storedPath, resources, MaxTextureBytes, resourceCache, cancellationToken).ConfigureAwait(false);
                        resourceGamePath = storedPath;
                    }
                    if (resolvedTexture is null)
                    {
                        warnings.Add($"Could not read texture dependency: {effectivePath}");
                        return null;
                    }
                    var textureLocator = CreateLocator(resourceGamePath, resolvedTexture.Value, candidates);
                    if (textureLocator is null)
                    {
                        warnings.Add($"Could not preserve the source location for texture: {resourceGamePath}");
                        return null;
                    }
                    textures.Add(new TextureDependency
                    {
                        StoredGamePath = storedPath,
                        EffectiveGamePath = effectivePath,
                        Flags = textureOffset.Flags,
                        Resource = textureLocator,
                    });
                }

                materials.Add(new MaterialDependency
                {
                    ModelMaterial = modelMaterial,
                    GamePath = materialPath,
                    Resource = materialLocator,
                    Textures = textures,
                });
            }

            return new ResourceDependencyManifest
            {
                Materials = materials,
                Manipulations = new System.Text.Json.Nodes.JsonArray(),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "Could not capture mashup resource dependencies.");
            warnings.Add($"Dependency capture failed: {e.Message}");
            return null;
        }
    }

    internal static IReadOnlyList<MaterialResourceCandidate> ScopeResourceCandidates(
        IReadOnlyCollection<MaterialResourceCandidate> candidates,
        IReadOnlyCollection<string>? preferredOptionMemberships,
        ICollection<string> warnings)
    {
        if (preferredOptionMemberships is null)
            return candidates.ToArray();

        var preferred = preferredOptionMemberships.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scoped = new List<MaterialResourceCandidate>();
        foreach (var group in candidates.GroupBy(
                     candidate => NormaliseGamePath(candidate.GamePath),
                     StringComparer.OrdinalIgnoreCase))
        {
            var unique = group
                .GroupBy(candidate => candidate.ActualPath, StringComparer.OrdinalIgnoreCase)
                .Select(items => items.First())
                .ToArray();
            var selected = unique.Where(candidate =>
                    candidate.OptionMemberships?.Any(preferred.Contains) == true)
                .ToArray();
            if (selected.Length == 0)
                selected = unique.Where(candidate =>
                        candidate.OptionMemberships?.Contains("default", StringComparer.OrdinalIgnoreCase) == true)
                    .ToArray();
            if (selected.Length == 0)
                selected = unique.Where(candidate => candidate.OptionMemberships is null ||
                                                      candidate.OptionMemberships.Count == 0)
                    .ToArray();

            if (selected.Length > 1)
            {
                var labels = selected
                    .Select(candidate => string.IsNullOrWhiteSpace(candidate.OptionLabel)
                        ? "Unmapped"
                        : candidate.OptionLabel!)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                warnings.Add(
                    $"Ambiguous dependency {group.Key} in options: {string.Join(", ", labels)}");
            }
            scoped.AddRange(selected);
        }
        return scoped;
    }

    private async Task<MaterialPreviewBundleResult> BuildAsync(
        byte[] modelBytes,
        string modelGamePath,
        IReadOnlyCollection<MaterialResourceCandidate> candidates,
        string bundleDirectory,
        bool excludeBodyAndGeneralMaterials = false,
        Dictionary<string, ResolvedResource?>? resourceCache = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        try
        {
            Directory.CreateDirectory(bundleDirectory);
            var textureDirectory = Path.Combine(bundleDirectory, "textures");
            Directory.CreateDirectory(textureDirectory);

            var resources = BuildResourceMap(candidates, warnings);
            var materialNames = ReadModelMaterials(modelBytes)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxMaterials + 1)
                .ToArray();
            if (materialNames.Length > MaxMaterials)
                throw new InvalidDataException($"The model references more than {MaxMaterials} materials.");
            var excludedMaterials = excludeBodyAndGeneralMaterials
                ? materialNames.Where(IsBodyOrGeneralMaterial).ToArray()
                : [];
            var excludedMaterialKeys = excludedMaterials.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var manifestMaterials = new List<object>();
            var textureCount = 0;
            long decodedBytes = 0;
            var writtenTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var modelMaterial in materialNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (excludedMaterialKeys.Contains(modelMaterial))
                    continue;
                var materialPath = ResolveMaterialPath(modelGamePath, modelMaterial, resources, warnings);
                if (materialPath is null)
                    continue;

                var materialBytes = await ReadResourceAsync(
                    materialPath, resources, MaxMaterialBytes, resourceCache, cancellationToken).ConfigureAwait(false);
                if (materialBytes is null)
                {
                    warnings.Add($"Material not found: {FileName(modelMaterial)}");
                    continue;
                }

                MtrlFile mtrl;
                MaterialMetadata metadata;
                try
                {
                    mtrl = LooseLuminaFile.Load<MtrlFile>(materialBytes);
                    metadata = ReadMaterialMetadata(materialBytes, mtrl);
                }
                catch (Exception e)
                {
                    warnings.Add($"Could not parse {FileName(modelMaterial)}: {e.Message}");
                    continue;
                }

                var textures = new List<object>();
                foreach (var sampler in metadata.Samplers)
                {
                    if (sampler.TextureIndex == byte.MaxValue || sampler.TextureIndex >= mtrl.TextureOffsets.Length)
                        continue;
                    if (++textureCount > MaxTextures)
                    {
                        warnings.Add($"Texture limit of {MaxTextures} reached.");
                        break;
                    }

                    var textureOffset = mtrl.TextureOffsets[sampler.TextureIndex];
                    var logicalPath = ReadString(mtrl.Strings, textureOffset.Offset);
                    if (!IsSafeGameResourcePath(logicalPath, ".tex"))
                    {
                        warnings.Add($"Invalid texture path in {FileName(modelMaterial)}.");
                        continue;
                    }
                    var resolvedTexturePath = Dx11TexturePath(logicalPath, textureOffset.Flags);
                    var textureBytes = await ReadResourceAsync(
                            resolvedTexturePath, resources, MaxTextureBytes, resourceCache, cancellationToken).ConfigureAwait(false)
                        ?? (resolvedTexturePath.Equals(logicalPath, StringComparison.OrdinalIgnoreCase) || resources.ContainsKey(resolvedTexturePath)
                            ? null
                            : await ReadResourceAsync(
                                logicalPath, resources, MaxTextureBytes, resourceCache, cancellationToken).ConfigureAwait(false));
                    if (textureBytes is null)
                    {
                        warnings.Add($"Texture not found: {FileName(logicalPath)}");
                        continue;
                    }

                    try
                    {
                        var tex = LooseLuminaFile.Load<TexFile>(textureBytes);
                        var width = (int)tex.Header.Width;
                        var height = (int)tex.Header.Height;
                        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
                            throw new InvalidDataException("texture dimensions are outside the supported range");
                        var rgba = tex.GetRgbaImageData();
                        var expected = checked(width * height * 4);
                        if (rgba.Length != expected)
                            throw new InvalidDataException("decoded texture byte count is invalid");
                        decodedBytes += rgba.Length;
                        if (decodedBytes > MaxDecodedBytes)
                            throw new InvalidDataException("decoded preview data exceeds 512 MiB");

                        var hash = Convert.ToHexString(SHA256.HashData(rgba)).ToLowerInvariant();
                        if (!writtenTextures.TryGetValue(hash, out var relativeFile))
                        {
                            relativeFile = $"textures/{hash}.rgba";
                            await File.WriteAllBytesAsync(Path.Combine(bundleDirectory, relativeFile.Replace('/', Path.DirectorySeparatorChar)), rgba, cancellationToken).ConfigureAwait(false);
                            writtenTextures.Add(hash, relativeFile);
                        }

                        var usage = TextureUsage(sampler.SamplerId);
                        textures.Add(new
                        {
                            usage,
                            samplerId = sampler.SamplerId,
                            samplerFlags = sampler.Flags,
                            gamePath = resolvedTexturePath,
                            file = relativeFile,
                            width,
                            height,
                            uvSet = TextureUvSet(sampler.SamplerId),
                            colorSpace = usage == "diffuse" ? "sRGB" : "Non-Color",
                        });
                    }
                    catch (Exception e)
                    {
                        warnings.Add($"Could not decode {FileName(logicalPath)}: {e.Message}");
                    }
                }

                manifestMaterials.Add(new
                {
                    modelMaterial,
                    gamePath = materialPath,
                    shaderPackage = ReadString(mtrl.Strings, mtrl.FileHeader.ShaderPackageNameOffset),
                    additionalData = ReadAdditionalData(materialBytes, mtrl),
                    shaderKeys = metadata.ShaderKeys,
                    shaderConstants = metadata.ShaderConstants,
                    colorSet = ReadColorSet(materialBytes, mtrl),
                    textures,
                });
            }

            var safeWarnings = BoundWarnings(warnings);
            var manifest = new
            {
                schema = Schema,
                version = Version,
                modelGamePath = NormaliseGamePath(modelGamePath),
                materials = manifestMaterials,
                excludedMaterials,
                warnings = safeWarnings,
            };
            var manifestPath = Path.Combine(bundleDirectory, "materials.json");
            await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return new MaterialPreviewBundleResult(manifestPath, safeWarnings);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "Could not build the Blender material preview bundle.");
            warnings.Add(e.Message);
            // When the directory itself is still usable, provide a valid empty
            // manifest. Blender can then import the geometry, surface the warning,
            // and safely remove the nonce directory after validating ownership.
            try
            {
                Directory.CreateDirectory(bundleDirectory);
                var manifestPath = Path.Combine(bundleDirectory, "materials.json");
                var safeWarnings = BoundWarnings(warnings);
                await WriteManifestAsync(
                    manifestPath,
                    new
                    {
                        schema = Schema,
                        version = Version,
                        modelGamePath = NormaliseGamePath(modelGamePath),
                        materials = Array.Empty<object>(),
                        excludedMaterials = Array.Empty<string>(),
                        warnings = safeWarnings,
                    },
                    cancellationToken).ConfigureAwait(false);
                return new MaterialPreviewBundleResult(manifestPath, safeWarnings);
            }
            catch (Exception manifestError) when (manifestError is not OperationCanceledException)
            {
                _log.Warning(manifestError, "Could not write the fallback Blender material preview manifest.");
                return new MaterialPreviewBundleResult(null, warnings);
            }
        }
    }

    private static IReadOnlyList<string> BoundWarnings(IEnumerable<string> warnings)
        => warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning.Length <= 512 ? warning : warning[..512])
            .Take(256)
            .ToArray();

    private static async Task WriteManifestAsync(string path, object manifest, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes)
            throw new InvalidDataException("The material preview manifest exceeds 1 MiB.");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, List<string>> BuildResourceMap(
        IEnumerable<MaterialResourceCandidate> candidates,
        ICollection<string> warnings)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var gamePath = NormaliseGamePath(candidate.GamePath);
            if (!IsSafeGameResourcePath(gamePath, ".mtrl", ".tex"))
                continue;
            if (!result.TryGetValue(gamePath, out var actualPaths))
                result.Add(gamePath, actualPaths = []);
            var actualPath = string.IsNullOrWhiteSpace(candidate.ActualPath) ? gamePath : candidate.ActualPath;
            if (!actualPaths.Contains(actualPath, StringComparer.OrdinalIgnoreCase))
                actualPaths.Add(actualPath);
        }
        foreach (var (gamePath, actualPaths) in result.Where(pair => pair.Value.Count > 1))
            warnings.Add($"Ambiguous resolved resource: {FileName(gamePath)}");
        return result;
    }

    internal static IReadOnlyList<string> ReadModelMaterials(byte[] bytes)
    {
        // Lumina builds bundled with some Dalamud versions still read the V6
        // model header as the older layout and can run past the end of otherwise
        // valid Dawntrail MDLs. The material strings are available in the
        // version-stable leading string table, before the layout diverges.
        const uint mdlV5 = 0x01000005;
        const uint mdlV6 = 0x01000006;
        const int fileHeaderSize = 68;
        const int vertexDeclarationSize = 17 * 8;
        const int stringHeaderSize = 8;
        const int maxVertexDeclarations = 256;
        const int maxStrings = 16_384;
        const int maxStringBytes = 16 * 1024 * 1024;

        if (bytes.Length < fileHeaderSize + stringHeaderSize)
            throw new InvalidDataException("MDL is too short to contain its string table.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        if (version is not (mdlV5 or mdlV6))
            throw new InvalidDataException($"Unsupported MDL version 0x{version:X8}.");

        var declarationCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2));
        var materialCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2));
        if (declarationCount > maxVertexDeclarations || materialCount > MaxMaterials)
            throw new InvalidDataException("MDL header counts exceed the supported limits.");

        var stringHeaderOffset = checked(fileHeaderSize + declarationCount * vertexDeclarationSize);
        if (stringHeaderOffset > bytes.Length - stringHeaderSize)
            throw new InvalidDataException("MDL string-table header is outside the file.");
        var stringCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(stringHeaderOffset, 2));
        var stringSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(stringHeaderOffset + 4, 4));
        if (stringCount > maxStrings || stringSize > maxStringBytes ||
            stringSize > bytes.Length - stringHeaderOffset - stringHeaderSize)
            throw new InvalidDataException("MDL string table exceeds the supported bounds.");

        var start = stringHeaderOffset + stringHeaderSize;
        var end = checked(start + (int)stringSize);
        var materials = new List<string>(materialCount);
        var strictUtf8 = new UTF8Encoding(false, true);
        for (var index = 0; index < stringCount; index++)
        {
            var terminator = Array.IndexOf(bytes, (byte)0, start, end - start);
            if (terminator < 0)
                throw new InvalidDataException("MDL string table contains an unterminated string.");
            var value = strictUtf8.GetString(bytes, start, terminator - start);
            if (value.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) &&
                IsSafeGameResourcePath(value, ".mtrl"))
                materials.Add(value);
            start = terminator + 1;
        }

        if (materials.Count != materialCount)
            throw new InvalidDataException(
                $"MDL declares {materialCount} materials but its string table contains {materials.Count} safe material paths.");
        return materials;
    }

    internal static string? ResolveMaterialPath(
        string modelGamePath,
        string modelMaterial,
        IReadOnlyDictionary<string, List<string>> resources,
        ICollection<string> warnings)
    {
        var normalizedMaterial = NormaliseGamePath(modelMaterial).TrimStart('/');
        if (normalizedMaterial.Contains('/') && IsSafeGameResourcePath(normalizedMaterial, ".mtrl") && resources.TryGetValue(normalizedMaterial, out var exactPaths))
        {
            if (exactPaths.Count == 1)
                return normalizedMaterial;
            warnings.Add($"Material path is ambiguous: {FileName(normalizedMaterial)}");
            return null;
        }

        var fileName = FileName(normalizedMaterial);
        var candidates = resources.Keys
            .Where(path => FileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 1 && resources[candidates[0]].Count == 1)
            return candidates[0];
        if (candidates.Length > 0)
        {
            warnings.Add($"Material path is ambiguous: {fileName}");
            return null;
        }

        var racialIdentity = RacialMaterialIdentity(fileName);
        if (racialIdentity is not null)
        {
            var racialCandidates = resources.Keys
                .Where(path => string.Equals(
                    RacialMaterialIdentity(FileName(path)), racialIdentity,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (racialCandidates.Length == 1 && resources[racialCandidates[0]].Count == 1)
                return racialCandidates[0];
            if (racialCandidates.Length > 0)
            {
                warnings.Add($"Material path is ambiguous after racial remapping: {fileName}");
                return null;
            }
        }

        var fallback = DeriveMaterialPath(modelGamePath, modelMaterial);
        if (!IsSafeGameResourcePath(fallback, ".mtrl"))
            return null;
        if (resources.TryGetValue(fallback, out var fallbackPaths) && fallbackPaths.Count != 1)
        {
            warnings.Add($"Material path is ambiguous: {fileName}");
            return null;
        }
        return fallback;
    }

    private static string? RacialMaterialIdentity(string fileName)
    {
        var match = RacialMaterialName.Match(fileName);
        return match.Success ? match.Groups["identity"].Value : null;
    }

    private async Task<byte[]?> ReadResourceAsync(
        string gamePath,
        IReadOnlyDictionary<string, List<string>> resources,
        long maxBytes,
        Dictionary<string, ResolvedResource?>? resourceCache,
        CancellationToken cancellationToken)
        => (await ResolveResourceAsync(gamePath, resources, maxBytes, resourceCache, cancellationToken)
            .ConfigureAwait(false))?.Bytes;

    private readonly record struct ResolvedResource(byte[] Bytes, string? ActualPath);

    private async Task<ResolvedResource?> ResolveResourceAsync(
        string gamePath,
        IReadOnlyDictionary<string, List<string>> resources,
        long maxBytes,
        Dictionary<string, ResolvedResource?>? resourceCache,
        CancellationToken cancellationToken)
    {
        gamePath = NormaliseGamePath(gamePath);
        var cacheKey = $"{maxBytes}:{gamePath}";
        if (resourceCache is not null && resourceCache.TryGetValue(cacheKey, out var cached))
            return cached;

        ResolvedResource? resolved = null;
        if (resources.TryGetValue(gamePath, out var actualPaths))
        {
            if (actualPaths.Count != 1)
                return null;
            var actual = actualPaths[0];
            if (Path.IsPathRooted(actual))
            {
                var info = new FileInfo(actual);
                if (!info.Exists || info.Length <= 0 || info.Length > maxBytes)
                    return null;
                resolved = new ResolvedResource(
                    await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false),
                    info.FullName);
            }
        }

        if (resolved is null)
        {
            try
            {
                var gameFile = await _data.GetFileAsync<FileResource>(gamePath, cancellationToken).ConfigureAwait(false);
                resolved = gameFile?.Data is { Length: > 0 } data && data.LongLength <= maxBytes
                    ? new ResolvedResource(data, null)
                    : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Missing or malformed game resources are expected for some custom
                // model material names. Keep the failure local to that material so
                // later materials can still contribute a preview.
                _log.Debug(e, "Could not read preview resource {GamePath}.", gamePath);
            }
        }

        if (resourceCache is not null)
            resourceCache[cacheKey] = resolved;
        return resolved;
    }

    private SourceResourceLocator? CreateLocator(
        string gamePath,
        ResolvedResource resource,
        IReadOnlyCollection<MaterialResourceCandidate> candidates)
    {
        var hash = Convert.ToHexString(SHA256.HashData(resource.Bytes)).ToLowerInvariant();
        if (resource.ActualPath is null)
        {
            return new SourceResourceLocator
            {
                Kind = "game",
                GamePath = NormaliseGamePath(gamePath),
                Sha256 = hash,
            };
        }

        var knownSource = candidates.FirstOrDefault(candidate =>
            string.Equals(NormaliseGamePath(candidate.GamePath), NormaliseGamePath(gamePath),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ActualPath, resource.ActualPath, StringComparison.OrdinalIgnoreCase));
        var knownLocator = CreateKnownSourceLocator(gamePath, knownSource, hash);
        if (knownLocator is not null)
            return knownLocator;

        var source = _sourceAttributor?.AttributionFor(resource.ActualPath);
        if (source is not { State: ResourceSourceState.LoadedMod } ||
            string.IsNullOrWhiteSpace(source.ModDirectory) ||
            string.IsNullOrWhiteSpace(source.RelativePath))
            return null;
        return new SourceResourceLocator
        {
            Kind = "mod",
            GamePath = NormaliseGamePath(gamePath),
            SourceModDirectory = source.ModDirectory,
            SourceModRootPath = source.ModRootPath,
            SourceRelativePath = source.RelativePath,
            Sha256 = hash,
        };
    }

    internal static SourceResourceLocator? CreateKnownSourceLocator(
        string gamePath,
        MaterialResourceCandidate? source,
        string hash)
    {
        if (source is null ||
            string.IsNullOrWhiteSpace(source.SourceModDirectory) ||
            string.IsNullOrWhiteSpace(source.SourceRelativePath))
            return null;
        return new SourceResourceLocator
        {
            Kind = "mod",
            GamePath = NormaliseGamePath(gamePath),
            SourceModDirectory = source.SourceModDirectory,
            SourceModRootPath = source.SourceModRootPath,
            SourceRelativePath = source.SourceRelativePath,
            Sha256 = hash,
        };
    }

    private static string DeriveMaterialPath(string mdlPath, string materialName)
    {
        mdlPath = NormaliseGamePath(mdlPath);
        materialName = "/" + FileName(materialName);
        var skin = System.Text.RegularExpressions.Regex.Match(materialName, @"^/mt_c(?<race>\d{4})b(?<body>\d{4})_.+\.mtrl$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (skin.Success)
            return $"chara/human/c{skin.Groups["race"].Value}/obj/body/b{skin.Groups["body"].Value}/material/v0001{materialName}";

        var modelDirectory = mdlPath[..Math.Max(0, mdlPath.LastIndexOf('/'))];
        var baseDirectory = modelDirectory[..Math.Max(0, modelDirectory.LastIndexOf('/'))];
        if (mdlPath.Contains("/face/f", StringComparison.OrdinalIgnoreCase) || mdlPath.Contains("/zear/z", StringComparison.OrdinalIgnoreCase))
            return $"{baseDirectory}/material{materialName}";
        return $"{baseDirectory}/material/v0001{materialName}";
    }

    private sealed record MaterialMetadata(
        ParsedSampler[] Samplers,
        object[] ShaderKeys,
        object[] ShaderConstants);

    private readonly record struct ParsedSampler(uint SamplerId, uint Flags, byte TextureIndex);

    /// <summary>
    /// Reads the shader section independently of Lumina's MtrlFile parser.
    /// Lumina versions shipped by some Dalamud installations assume the old
    /// colorset sizes and position the material header incorrectly for the
    /// Dawntrail 0x800/0x880 datasets. The leading file header, string table,
    /// and texture offsets remain usable, but its Samplers array is then empty.
    /// </summary>
    private static MaterialMetadata ReadMaterialMetadata(byte[] bytes, MtrlFile mtrl)
    {
        const int materialHeaderSize = 12;
        const int shaderKeySize = 8;
        const int constantSize = 8;
        const int samplerSize = 12;
        const int maxShaderKeys = 256;
        const int maxConstants = 256;
        const int maxSamplers = 64;

        var headerOffset = checked(DataSetOffset(mtrl) + mtrl.FileHeader.DataSetSize);
        if (headerOffset < 0 || headerOffset + materialHeaderSize > bytes.Length)
            throw new InvalidDataException("material shader header is outside the file");

        var shaderValueByteSize = BitConverter.ToUInt16(bytes, headerOffset);
        var shaderKeyCount = BitConverter.ToUInt16(bytes, headerOffset + 2);
        var constantCount = BitConverter.ToUInt16(bytes, headerOffset + 4);
        var samplerCount = BitConverter.ToUInt16(bytes, headerOffset + 6);
        if (shaderKeyCount > maxShaderKeys || constantCount > maxConstants || samplerCount > maxSamplers)
            throw new InvalidDataException("material shader metadata exceeds the supported limits");
        if (shaderValueByteSize % sizeof(float) != 0)
            throw new InvalidDataException("material shader value data is not float-aligned");

        var cursor = checked(headerOffset + materialHeaderSize);
        var requiredSize = checked(
            shaderKeyCount * shaderKeySize
            + constantCount * constantSize
            + samplerCount * samplerSize
            + shaderValueByteSize);
        if (cursor + requiredSize > bytes.Length)
            throw new InvalidDataException("material shader metadata is truncated");

        var shaderKeys = new object[shaderKeyCount];
        for (var i = 0; i < shaderKeyCount; i++, cursor += shaderKeySize)
        {
            var category = BitConverter.ToUInt32(bytes, cursor);
            var value = BitConverter.ToUInt32(bytes, cursor + 4);
            shaderKeys[i] = new { category, value };
        }

        var constants = new (uint Id, ushort Offset, ushort Size)[constantCount];
        for (var i = 0; i < constantCount; i++, cursor += constantSize)
        {
            constants[i] = (
                BitConverter.ToUInt32(bytes, cursor),
                BitConverter.ToUInt16(bytes, cursor + 4),
                BitConverter.ToUInt16(bytes, cursor + 6));
        }

        var samplers = new ParsedSampler[samplerCount];
        for (var i = 0; i < samplerCount; i++, cursor += samplerSize)
        {
            samplers[i] = new ParsedSampler(
                BitConverter.ToUInt32(bytes, cursor),
                BitConverter.ToUInt32(bytes, cursor + 4),
                bytes[cursor + 8]);
        }

        var shaderValuesOffset = cursor;
        var shaderConstants = new object[constantCount];
        for (var i = 0; i < constants.Length; i++)
        {
            var constant = constants[i];
            if (constant.Offset % sizeof(float) != 0 || constant.Size % sizeof(float) != 0 ||
                constant.Offset + constant.Size > shaderValueByteSize)
                throw new InvalidDataException("material shader constant points outside the value data");
            var count = constant.Size / sizeof(float);
            var values = new float[count];
            for (var j = 0; j < count; j++)
                values[j] = BitConverter.ToSingle(bytes, shaderValuesOffset + constant.Offset + j * sizeof(float));
            shaderConstants[i] = new { id = constant.Id, values };
        }

        return new MaterialMetadata(samplers, shaderKeys, shaderConstants);
    }

    internal static IReadOnlyList<string> ReadTextureUsages(byte[] materialBytes)
    {
        try
        {
            var mtrl = LooseLuminaFile.Load<MtrlFile>(materialBytes);
            var usages = Enumerable.Repeat("other", mtrl.TextureOffsets.Length).ToArray();
            var metadata = ReadMaterialMetadata(materialBytes, mtrl);

            foreach (var sampler in metadata.Samplers)
            {
                if (sampler.TextureIndex == byte.MaxValue || sampler.TextureIndex >= usages.Length)
                    continue;
                var usage = TextureUsage(sampler.SamplerId);
                if (usages[sampler.TextureIndex] == "other" || usage != "other")
                    usages[sampler.TextureIndex] = usage;
            }
            return usages;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int DataSetOffset(MtrlFile mtrl)
        => checked(16
            + 4 * (mtrl.FileHeader.TextureCount + mtrl.FileHeader.UvSetCount + mtrl.FileHeader.ColorSetCount)
            + mtrl.FileHeader.StringTableSize
            + mtrl.FileHeader.AdditionalDataSize);

    private static object? ReadColorSet(byte[] bytes, MtrlFile mtrl)
    {
        var size = mtrl.FileHeader.DataSetSize;
        var (tableSize, width, height) = size switch
        {
            512 => (512, 4, 16),       // Legacy 16-row colorset.
            1024 => (1024, 4, 32),     // Legacy 32-row colorset.
            2048 => (2048, 8, 32),     // Dawntrail expanded colorset.
            2176 => (2048, 8, 32),     // Expanded colorset + 128-byte dye table.
            _ => (0, 0, 0),
        };
        if (tableSize == 0)
            return null;
        var offset = DataSetOffset(mtrl);
        if (offset < 0 || offset + size > bytes.Length)
            return null;
        var values = new float[tableSize / 2];
        for (var i = 0; i < values.Length; i++)
            values[i] = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, offset + i * 2));
        return new { width, height, values };
    }

    private static string ReadAdditionalData(byte[] bytes, MtrlFile mtrl)
    {
        var size = mtrl.FileHeader.AdditionalDataSize;
        var offset = 16 + 4 * (mtrl.FileHeader.TextureCount + mtrl.FileHeader.UvSetCount + mtrl.FileHeader.ColorSetCount)
                     + mtrl.FileHeader.StringTableSize;
        return size > 0 && offset >= 0 && offset + size <= bytes.Length
            ? Convert.ToHexString(bytes.AsSpan(offset, size)).ToLowerInvariant()
            : string.Empty;
    }

    private static string TextureUsage(uint samplerId)
        => samplerId switch
        {
            0x0C5EC1F1 or 0xAAB4D9E9 or 0xDDB3E97F or 0x0261CDCB or 0x92F03E53 => "normal",
            0x8A4E82B6 or 0xB3F13975 or 0x800BE99B => "mask",
            0x565F8FD8 => "index",
            0x115306BE or 0x1E6FEF9C or 0x6968DF0A => "diffuse",
            0x2B99E025 or 0x1BBC2F12 or 0x6CBB1F84 => "specular",
            0x32667BD7 => "occlusion",
            0xA7E197F6 => "flow",
            0x0237CB94 => "decal",
            _ => "other",
        };

    private static int TextureUvSet(uint samplerId)
        => samplerId is 0xDDB3E97F or 0x6CBB1F84 or 0x6968DF0A or 0xE5338C17 ? 1 : 0;

    private static string Dx11TexturePath(string path, ushort flags)
        => PathRules.Dx11TexturePath(path, flags);

    private static string ReadString(byte[] strings, int offset)
        => PathRules.ReadNullTerminated(strings, offset);

    private static string NormaliseGamePath(string path)
        => PathRules.NormalizeGamePath(path);

    private static string FileName(string path)
        => NormaliseGamePath(path).Split('/').LastOrDefault() ?? string.Empty;

    private static bool IsBodyOrGeneralMaterial(string materialName)
    {
        var fileName = FileName(materialName);
        return StandardBodyMaterial.IsMatch(fileName) ||
               fileName.Contains("piercing", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("pube", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeGameResourcePath(string path, params string[] extensions)
    {
        path = NormaliseGamePath(path);
        return path.Length is > 0 and <= 4096 &&
               !Path.IsPathRooted(path) &&
               !path.Split('/').Any(segment => segment is "" or "." or "..") &&
               extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    internal static class LooseLuminaFile
    {
        private static readonly PropertyInfo DataProperty = typeof(FileResource).GetProperty(nameof(FileResource.Data), BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(typeof(FileResource).FullName, nameof(FileResource.Data));
        private static readonly PropertyInfo ReaderProperty = typeof(FileResource).GetProperty(nameof(FileResource.Reader), BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(typeof(FileResource).FullName, nameof(FileResource.Reader));

        public static T Load<T>(byte[] bytes) where T : FileResource, new()
        {
            if (bytes.Length == 0)
                throw new InvalidDataException("resource is empty");
            var file = new T();
            DataProperty.SetValue(file, bytes);
            ReaderProperty.SetValue(file, new LuminaBinaryReader(bytes, PlatformId.Win32));
            file.LoadFile();
            return file;
        }
    }
}
