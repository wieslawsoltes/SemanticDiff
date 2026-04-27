using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed class GitHistoryService : IGitHistoryService
{
    private const char UnitSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';
    private readonly IGitCommandRunner commandRunner;

    public GitHistoryService()
        : this(new GitCommandRunner())
    {
    }

    public GitHistoryService(IGitCommandRunner commandRunner)
    {
        this.commandRunner = commandRunner;
    }

    public async Task<GitHistorySnapshot> GetHistoryAsync(GitHistoryRequest request, CancellationToken cancellationToken)
    {
        var normalizedRequest = request with
        {
            MaxCount = Math.Clamp(request.MaxCount, 1, 1_000),
            Skip = Math.Max(0, request.Skip)
        };
        var result = await commandRunner.RunAsync(
            normalizedRequest.RepositoryPath,
            BuildLogArguments(normalizedRequest),
            cancellationToken).ConfigureAwait(false);

        var commits = result.Succeeded
            ? ParseCommits(result.StandardOutput)
            : ImmutableArray<GitCommitInfo>.Empty;
        return new GitHistorySnapshot(normalizedRequest, commits, result.Succeeded && commits.Length == normalizedRequest.MaxCount, DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<string> BuildLogArguments(GitHistoryRequest request)
    {
        var revision = BuildRevision(request);
        var arguments = new List<string>
        {
            "log",
            "--topo-order",
            $"--max-count={request.MaxCount}",
            $"--skip={request.Skip}",
            "--date=iso-strict",
            $"--pretty=format:%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%D%x1f%s"
        };

        if (!string.IsNullOrWhiteSpace(revision))
        {
            arguments.Add(revision);
        }

        if (!string.IsNullOrWhiteSpace(request.PathFilter))
        {
            arguments.Add("--");
            arguments.Add(request.PathFilter);
        }

        return arguments;
    }

    private static string BuildRevision(GitHistoryRequest request)
    {
        var headRef = NormalizeRef(request.HeadRef) ?? "HEAD";
        var baseRef = NormalizeRef(request.BaseRef);
        return baseRef is null ? headRef : $"{baseRef}..{headRef}";
    }

    private static ImmutableArray<GitCommitInfo> ParseCommits(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ImmutableArray<GitCommitInfo>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<GitCommitInfo>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r', RecordSeparator);
            var parts = line.Split(UnitSeparator);
            if (parts.Length < 8 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            var parentIds = string.IsNullOrWhiteSpace(parts[2])
                ? ImmutableArray<string>.Empty
                : parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();

            builder.Add(new GitCommitInfo(
                parts[0].Trim(),
                string.IsNullOrWhiteSpace(parts[1]) ? ShortenCommit(parts[0]) : parts[1].Trim(),
                parentIds,
                parts[3].Trim(),
                parts[4].Trim(),
                DateTimeOffset.TryParse(parts[5], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var authorTime)
                    ? authorTime
                    : null,
                parts[6].Trim(),
                parts[7].Trim()));
        }

        return builder.ToImmutable();
    }

    private static string? NormalizeRef(string? reference) => string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

    private static string ShortenCommit(string commitId) => commitId.Length <= 12 ? commitId : commitId[..12];
}
