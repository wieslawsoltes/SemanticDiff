using System.Collections.Immutable;
using System.Text.RegularExpressions;
using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed partial class GitPatchSeriesComparisonService : IGitPatchSeriesComparisonService
{
    private const char FieldSeparator = '\u001f';
    private readonly IGitCommandRunner commandRunner;

    public GitPatchSeriesComparisonService()
        : this(new GitCommandRunner())
    {
    }

    public GitPatchSeriesComparisonService(IGitCommandRunner commandRunner)
    {
        this.commandRunner = commandRunner;
    }

    public async Task<GitPatchSeriesComparisonSnapshot> CompareAsync(
        GitPatchSeriesComparisonRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OldRange);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NewRange);

        var oldSeriesTask = LoadSeriesAsync(request.RepositoryPath, request.OldRange, cancellationToken);
        var newSeriesTask = LoadSeriesAsync(request.RepositoryPath, request.NewRange, cancellationToken);
        var rangeDiffTask = LoadRangeDiffAsync(request, cancellationToken);

        await Task.WhenAll((Task)oldSeriesTask, newSeriesTask, rangeDiffTask).ConfigureAwait(false);

        var oldSeries = await oldSeriesTask.ConfigureAwait(false);
        var newSeries = await newSeriesTask.ConfigureAwait(false);
        var rangeDiffResult = await rangeDiffTask.ConfigureAwait(false);
        var items = ParseRangeDiff(rangeDiffResult.StandardOutput);
        var status = CreateStatusMessage(rangeDiffResult, oldSeries, newSeries, items);

        return new GitPatchSeriesComparisonSnapshot(
            request,
            oldSeries,
            newSeries,
            items,
            rangeDiffResult.StandardOutput,
            status,
            DateTimeOffset.UtcNow);
    }

    internal static ImmutableArray<GitPatchSeriesComparisonItem> ParseRangeDiff(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ImmutableArray<GitPatchSeriesComparisonItem>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<GitPatchSeriesComparisonItem>();
        GitPatchSeriesComparisonItem? current = null;
        var detailLines = ImmutableArray.CreateBuilder<string>();

        foreach (var line in SplitLines(output))
        {
            var match = RangeDiffHeaderRegex().Match(line);
            if (match.Success)
            {
                FlushCurrent();
                current = CreateItem(match);
                continue;
            }

            if (current is not null)
            {
                detailLines.Add(line);
            }
        }

        FlushCurrent();
        return builder.ToImmutable();

        void FlushCurrent()
        {
            if (current is null)
            {
                detailLines.Clear();
                return;
            }

            var details = detailLines.ToImmutable();
            builder.Add(current with
            {
                DetailLines = details,
                DetailText = CreateDetailText(details)
            });
            current = null;
            detailLines.Clear();
        }
    }

    private async Task<GitPatchSeriesInfo> LoadSeriesAsync(
        string repositoryPath,
        string rangeText,
        CancellationToken cancellationToken)
    {
        var commitTask = commandRunner.RunAsync(
            repositoryPath,
            ["log", "--reverse", $"--format=%H%x1f%h%x1f%an%x1f%aI%x1f%s", rangeText],
            cancellationToken);
        var filesTask = commandRunner.RunAsync(
            repositoryPath,
            ["diff", "--name-status", "-z", rangeText],
            cancellationToken);

        await Task.WhenAll(commitTask, filesTask).ConfigureAwait(false);

        var commitsResult = await commitTask.ConfigureAwait(false);
        var filesResult = await filesTask.ConfigureAwait(false);
        return new GitPatchSeriesInfo(
            rangeText,
            commitsResult.Succeeded ? ParseCommits(commitsResult.StandardOutput) : [],
            filesResult.Succeeded ? ParseNameStatus(filesResult.StandardOutput) : []);
    }

    private Task<GitCommandResult> LoadRangeDiffAsync(
        GitPatchSeriesComparisonRequest request,
        CancellationToken cancellationToken) =>
        commandRunner.RunAsync(
            request.RepositoryPath,
            ["range-diff", "--no-color", request.OldRange, request.NewRange],
            cancellationToken);

    private static GitPatchSeriesComparisonItem CreateItem(Match match)
    {
        var oldIndex = ParseIndex(match.Groups["oldIndex"].Value);
        var newIndex = ParseIndex(match.Groups["newIndex"].Value);
        var oldCommit = NormalizeCommit(match.Groups["oldCommit"].Value);
        var newCommit = NormalizeCommit(match.Groups["newCommit"].Value);
        var marker = match.Groups["marker"].Value;
        var kind = marker switch
        {
            "=" => GitPatchSeriesComparisonKind.Unchanged,
            "!" => GitPatchSeriesComparisonKind.Modified,
            "<" => GitPatchSeriesComparisonKind.Removed,
            ">" => GitPatchSeriesComparisonKind.Added,
            _ => GitPatchSeriesComparisonKind.Unknown
        };

        return new GitPatchSeriesComparisonItem(
            kind,
            oldIndex,
            oldCommit,
            newIndex,
            newCommit,
            match.Groups["subject"].Value.Trim(),
            string.Empty,
            ImmutableArray<string>.Empty);
    }

    private static ImmutableArray<GitPatchCommitInfo> ParseCommits(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ImmutableArray<GitPatchCommitInfo>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<GitPatchCommitInfo>();
        foreach (var line in SplitLines(output))
        {
            var parts = line.Split(FieldSeparator);
            if (parts.Length < 5)
            {
                continue;
            }

            builder.Add(new GitPatchCommitInfo(
                parts[0],
                parts[1],
                parts[2],
                DateTimeOffset.TryParse(parts[3], out var authorTime) ? authorTime : null,
                parts[4]));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<GitFileChange> ParseNameStatus(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return ImmutableArray<GitFileChange>.Empty;
        }

        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var builder = ImmutableArray.CreateBuilder<GitFileChange>();

        for (var tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
        {
            var statusToken = tokens[tokenIndex];
            if (statusToken.Length == 0 || tokenIndex + 1 >= tokens.Length)
            {
                continue;
            }

            var status = ParseStatus(statusToken[0]);
            string? oldPath = null;
            var path = tokens[++tokenIndex];

            if (status is DiffFileStatus.Renamed or DiffFileStatus.Copied && tokenIndex + 1 < tokens.Length)
            {
                oldPath = path;
                path = tokens[++tokenIndex];
            }

            builder.Add(new GitFileChange(path, oldPath, status, 0, 0, LanguageFromPath(path)));
        }

        return builder.ToImmutable();
    }

    private static string CreateStatusMessage(
        GitCommandResult rangeDiffResult,
        GitPatchSeriesInfo oldSeries,
        GitPatchSeriesInfo newSeries,
        ImmutableArray<GitPatchSeriesComparisonItem> items)
    {
        if (!rangeDiffResult.Succeeded && string.IsNullOrWhiteSpace(rangeDiffResult.StandardOutput))
        {
            var error = string.IsNullOrWhiteSpace(rangeDiffResult.StandardError)
                ? "range-diff failed"
                : rangeDiffResult.StandardError.Trim();
            return $"Patch comparison failed: {error}";
        }

        if (items.IsDefaultOrEmpty)
        {
            return $"No range-diff rows | old {oldSeries.CommitCount:N0} patches, new {newSeries.CommitCount:N0} patches";
        }

        var unchanged = items.Count(item => item.Kind == GitPatchSeriesComparisonKind.Unchanged);
        var modified = items.Count(item => item.Kind == GitPatchSeriesComparisonKind.Modified);
        var removed = items.Count(item => item.Kind == GitPatchSeriesComparisonKind.Removed);
        var added = items.Count(item => item.Kind == GitPatchSeriesComparisonKind.Added);
        return $"{unchanged:N0} unchanged | {modified:N0} modified | {removed:N0} old-only | {added:N0} new-only";
    }

    private static int? ParseIndex(string value) =>
        int.TryParse(value, out var result) ? result : null;

    private static string? NormalizeCommit(string value) =>
        string.IsNullOrWhiteSpace(value) || value.All(character => character == '-')
            ? null
            : value;

    private static string CreateDetailText(ImmutableArray<string> detailLines)
    {
        if (detailLines.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var nonEmpty = detailLines
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return nonEmpty ?? string.Empty;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static DiffFileStatus ParseStatus(char status) => status switch
    {
        'A' => DiffFileStatus.Added,
        'D' => DiffFileStatus.Deleted,
        'R' => DiffFileStatus.Renamed,
        'C' => DiffFileStatus.Copied,
        'U' => DiffFileStatus.Conflicted,
        _ => DiffFileStatus.Modified
    };

    private static string LanguageFromPath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" => "C#",
            ".xaml" => "XAML",
            ".axaml" => "AXAML",
            ".xml" => "XML",
            _ => extension.TrimStart('.').ToUpperInvariant()
        };
    }

    [GeneratedRegex(@"^\s*(?<oldIndex>\d+|-):\s+(?<oldCommit>[0-9a-fA-F]+|-+)\s+(?<marker>[=!<>])\s+(?<newIndex>\d+|-):\s+(?<newCommit>[0-9a-fA-F]+|-+)\s*(?<subject>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RangeDiffHeaderRegex();
}
