using System.Collections.Immutable;

namespace SemanticDiff.Core;

public static class DiffFileTypeClassifier
{
    public const string NoExtensionKey = "<none>";

    public static string GetFileTypeKey(string? path, string? language = null)
    {
        var extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path).Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension;
        }

        var normalizedLanguage = NormalizeLanguage(language);
        return string.IsNullOrWhiteSpace(normalizedLanguage)
            ? NoExtensionKey
            : $"language:{normalizedLanguage}";
    }

    public static string FormatFileTypeName(string key, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "Unknown";
        }

        if (key.StartsWith("language:", StringComparison.Ordinal))
        {
            var languageName = key["language:".Length..];
            return string.IsNullOrWhiteSpace(languageName) ? "No extension" : languageName.ToUpperInvariant();
        }

        if (string.Equals(key, NoExtensionKey, StringComparison.Ordinal))
        {
            return "No extension";
        }

        var displayLanguage = string.IsNullOrWhiteSpace(language) ? string.Empty : $" {language.Trim()}";
        return $"{key}{displayLanguage}";
    }

    public static ImmutableHashSet<string>? NormalizeIncludedFileTypeKeys(IEnumerable<string>? keys)
    {
        if (keys is null)
        {
            return null;
        }

        return keys
            .Select(NormalizeFileTypeKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeFileTypeKey(string key) => (key ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsIncluded(
        string path,
        string? oldPath,
        string? language,
        IImmutableSet<string>? includedFileTypeKeys)
    {
        if (includedFileTypeKeys is null)
        {
            return true;
        }

        if (includedFileTypeKeys.Count == 0)
        {
            return false;
        }

        var pathKey = GetFileTypeKey(path, language);
        if (includedFileTypeKeys.Contains(pathKey))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(oldPath) && includedFileTypeKeys.Contains(GetFileTypeKey(oldPath, language));
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        return language.Trim().ToLowerInvariant() switch
        {
            "c#" => "cs",
            "f#" => "fs",
            "visual basic" => "vb",
            var value => value
        };
    }
}
