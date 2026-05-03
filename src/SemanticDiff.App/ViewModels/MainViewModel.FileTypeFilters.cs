using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Workbench.Workspace;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel
{
    private RepositoryDiffLoadResult ApplyFileTypeFilter(RepositoryDiffLoadResult loadedDiff, bool refreshOptions)
    {
        if (refreshOptions)
        {
            UpdateFileTypeFilterOptions(loadedDiff.GitSnapshot.Files);
        }

        var includedFileTypeKeys = GetIncludedFileTypeKeySet();
        if (includedFileTypeKeys is null)
        {
            return loadedDiff;
        }

        var filteredFiles = loadedDiff.GitSnapshot.Files
            .Where(file => DiffFileTypeClassifier.IsIncluded(file.Path, file.OldPath, file.Language, includedFileTypeKeys))
            .ToImmutableArray();
        var filteredDocuments = loadedDiff.Documents
            .Where(document => DiffFileTypeClassifier.IsIncluded(
                document.Metadata.Path,
                document.Metadata.OldPath,
                document.Metadata.Language,
                includedFileTypeKeys))
            .ToImmutableArray();

        return loadedDiff with
        {
            GitSnapshot = loadedDiff.GitSnapshot with { Files = filteredFiles },
            Documents = filteredDocuments
        };
    }

    private void UpdateFileTypeFilterOptions(ImmutableArray<GitFileChange> files)
    {
        var includedFileTypeKeys = GetIncludedFileTypeKeySet();
        var options = files
            .GroupBy(file => DiffFileTypeClassifier.GetFileTypeKey(file.Path, file.Language), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var language = group
                    .Select(file => file.Language)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                return new FileTypeFilterOptionViewModel(
                    DiffFileTypeClassifier.NormalizeFileTypeKey(group.Key),
                    DiffFileTypeClassifier.FormatFileTypeName(group.Key, language),
                    group.Count(),
                    includedFileTypeKeys is null || includedFileTypeKeys.Contains(group.Key));
            })
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        FileTypeFilterOptions.Clear();
        foreach (var option in options)
        {
            FileTypeFilterOptions.Add(option);
        }

        UpdateFileTypeFilterSummaryText();
    }

    private void RefreshFileTypeFilterPresentation()
    {
        var includedFileTypeKeys = GetIncludedFileTypeKeySet();
        if (FileTypeFilterOptions.Count > 0)
        {
            var refreshedOptions = FileTypeFilterOptions
                .Select(option => option with
                {
                    IsSelected = includedFileTypeKeys is null || includedFileTypeKeys.Contains(option.Key)
                })
                .ToArray();
            FileTypeFilterOptions.Clear();
            foreach (var option in refreshedOptions)
            {
                FileTypeFilterOptions.Add(option);
            }
        }

        UpdateFileTypeFilterSummaryText();
    }

    private void UpdateFileTypeFilterSummaryText()
    {
        var includedFileTypeKeys = GetIncludedFileTypeKeySet();
        if (includedFileTypeKeys is null)
        {
            FileTypeFilterSummaryText = "All file types";
            return;
        }

        if (includedFileTypeKeys.Count == 0)
        {
            FileTypeFilterSummaryText = "No file types";
            return;
        }

        var selectedCount = FileTypeFilterOptions.Count == 0
            ? includedFileTypeKeys.Count
            : FileTypeFilterOptions.Count(option => option.IsSelected);
        FileTypeFilterSummaryText = FileTypeFilterOptions.Count == 0
            ? $"{selectedCount:N0} file types"
            : $"{selectedCount:N0}/{FileTypeFilterOptions.Count:N0} file types";
    }

    private ImmutableHashSet<string>? GetIncludedFileTypeKeySet() =>
        DiffFileTypeClassifier.NormalizeIncludedFileTypeKeys(appState.IncludedFileTypeKeys);

    private string CreateFileTypeFilterCacheKey()
    {
        var includedFileTypeKeys = appState.IncludedFileTypeKeys;
        if (includedFileTypeKeys is null)
        {
            return "all";
        }

        var normalized = includedFileTypeKeys
            .Select(DiffFileTypeClassifier.NormalizeFileTypeKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? "none" : string.Join(',', normalized);
    }

    private string FormatFileTypeFilterStatus(int filteredFileCount, int totalFileCount) =>
        appState.IncludedFileTypeKeys is null
            ? string.Empty
            : $" | file types {filteredFileCount:N0}/{totalFileCount:N0}";

    private static string[] NormalizeFileTypeFilterKeys(IEnumerable<string> keys) =>
        keys
            .Select(DiffFileTypeClassifier.NormalizeFileTypeKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool FileTypeFilterKeysEqual(string[]? first, string[]? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        var normalizedFirst = NormalizeFileTypeFilterKeys(first);
        var normalizedSecond = NormalizeFileTypeFilterKeys(second);
        return normalizedFirst.SequenceEqual(normalizedSecond, StringComparer.OrdinalIgnoreCase);
    }
}
