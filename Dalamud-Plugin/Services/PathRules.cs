using System.Text;

namespace InstantEdit.Services;

/// <summary>Shared normalization and containment rules for plugin-controlled paths.</summary>
internal static class PathRules
{
    public static bool IsPathWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNormalizeRelativePath(string? path, out string normalized)
    {
        normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        return normalized.Length is > 0 and <= 4096 &&
               !normalized.Contains('\0') &&
               !Path.IsPathRooted(normalized) &&
               normalized.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    public static bool IsSafeVariantName(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && value is not ("." or "..") &&
           !value.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) &&
           value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           !value.Contains('/') && !value.Contains('\\') &&
           value.All(c => !char.IsControl(c));

    public static string NormalizeGamePath(string? value)
        => (value ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    public static string Dx11TexturePath(string path, ushort flags)
    {
        var normalized = NormalizeGamePath(path);
        if ((flags & 0x8000) == 0)
            return normalized;
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? "--" + normalized : normalized[..(separator + 1)] + "--" + normalized[(separator + 1)..];
    }

    public static string ReadNullTerminated(byte[] strings, int offset)
    {
        if (offset < 0 || offset >= strings.Length)
            return string.Empty;
        var end = Array.IndexOf(strings, (byte)0, offset);
        if (end < 0)
            end = strings.Length;
        return Encoding.UTF8.GetString(strings, offset, end - offset);
    }
}
