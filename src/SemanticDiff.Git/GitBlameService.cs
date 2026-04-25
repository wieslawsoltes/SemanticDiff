using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed class GitBlameService : IGitBlameService
{
    private readonly IGitCommandRunner commandRunner;

    public GitBlameService()
        : this(new GitCommandRunner())
    {
    }

    public GitBlameService(IGitCommandRunner commandRunner)
    {
        this.commandRunner = commandRunner;
    }

    public async Task<GitFileBlame> GetFileBlameAsync(string repositoryPath, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(path))
        {
            return GitFileBlame.Empty(path);
        }

        var result = await commandRunner.RunAsync(repositoryPath, ["blame", "--line-porcelain", "--", path], cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Parse(path, result.StandardOutput) : GitFileBlame.Empty(path);
    }

    public static GitFileBlame Parse(string path, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return GitFileBlame.Empty(path);
        }

        var builder = ImmutableArray.CreateBuilder<GitBlameLine>();
        var commitId = string.Empty;
        var author = string.Empty;
        var summary = string.Empty;
        DateTimeOffset? authorTime = null;
        var lineNumber = 0;

        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                continue;
            }

            if (rawLine[0] == '\t')
            {
                builder.Add(new GitBlameLine(
                    lineNumber <= 0 ? builder.Count + 1 : lineNumber,
                    commitId,
                    string.IsNullOrWhiteSpace(author) ? "Unknown" : author,
                    authorTime,
                    summary));
                continue;
            }

            if (TryParseHeader(rawLine, out var parsedCommitId, out var parsedLineNumber))
            {
                commitId = parsedCommitId;
                lineNumber = parsedLineNumber;
                author = string.Empty;
                summary = string.Empty;
                authorTime = null;
                continue;
            }

            if (rawLine.StartsWith("author ", StringComparison.Ordinal))
            {
                author = rawLine["author ".Length..];
            }
            else if (rawLine.StartsWith("author-time ", StringComparison.Ordinal) && long.TryParse(rawLine["author-time ".Length..], out var unixTime))
            {
                authorTime = DateTimeOffset.FromUnixTimeSeconds(unixTime);
            }
            else if (rawLine.StartsWith("summary ", StringComparison.Ordinal))
            {
                summary = rawLine["summary ".Length..];
            }
        }

        return new GitFileBlame(path, builder.ToImmutable());
    }

    private static bool TryParseHeader(string line, out string commitId, out int lineNumber)
    {
        commitId = string.Empty;
        lineNumber = 0;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !IsCommitToken(parts[0]) || !int.TryParse(parts[2], out lineNumber))
        {
            return false;
        }

        commitId = parts[0].TrimStart('^');
        return true;
    }

    private static bool IsCommitToken(string token)
    {
        var normalized = token.TrimStart('^');
        return normalized.Length >= 7 && normalized.All(Uri.IsHexDigit);
    }
}