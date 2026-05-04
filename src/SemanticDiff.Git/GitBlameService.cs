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

    public async Task<GitFileBlame> GetFileBlameAsync(string repositoryPath, string path, CancellationToken cancellationToken, string? revision = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(path))
        {
            return GitFileBlame.Empty(path);
        }

        var arguments = new List<string>
        {
            "blame",
            "--line-porcelain"
        };
        if (!string.IsNullOrWhiteSpace(revision))
        {
            arguments.Add(revision.Trim());
        }

        arguments.Add("--");
        arguments.Add(path);

        var result = await commandRunner.RunAsync(repositoryPath, arguments, cancellationToken).ConfigureAwait(false);
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

        var lineStart = 0;
        while (TryReadLine(output, ref lineStart, out var rawLine))
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
                    summary,
                    rawLine[1..].ToString()));
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

            if (rawLine.StartsWith("author ".AsSpan(), StringComparison.Ordinal))
            {
                author = rawLine["author ".Length..].ToString();
            }
            else if (rawLine.StartsWith("author-time ".AsSpan(), StringComparison.Ordinal) && long.TryParse(rawLine["author-time ".Length..], out var unixTime))
            {
                authorTime = DateTimeOffset.FromUnixTimeSeconds(unixTime);
            }
            else if (rawLine.StartsWith("summary ".AsSpan(), StringComparison.Ordinal))
            {
                summary = rawLine["summary ".Length..].ToString();
            }
        }

        return new GitFileBlame(path, builder.ToImmutable());
    }

    private static bool TryReadLine(string text, ref int lineStart, out ReadOnlySpan<char> line)
    {
        if (lineStart >= text.Length)
        {
            line = ReadOnlySpan<char>.Empty;
            return false;
        }

        var newlineOffset = text.AsSpan(lineStart).IndexOfAny('\r', '\n');
        if (newlineOffset < 0)
        {
            line = text.AsSpan(lineStart);
            lineStart = text.Length;
            return true;
        }

        var lineEnd = lineStart + newlineOffset;
        line = text.AsSpan(lineStart, lineEnd - lineStart);
        lineStart = lineEnd + 1;
        if (text[lineEnd] == '\r' && lineStart < text.Length && text[lineStart] == '\n')
        {
            lineStart++;
        }

        return true;
    }

    private static bool TryParseHeader(ReadOnlySpan<char> line, out string commitId, out int lineNumber)
    {
        commitId = string.Empty;
        lineNumber = 0;
        if (!TryReadPart(line, 0, out var commitToken, out var nextIndex) ||
            !TryReadPart(line, nextIndex, out _, out nextIndex) ||
            !TryReadPart(line, nextIndex, out var finalLineToken, out _) ||
            !IsCommitToken(commitToken) ||
            !int.TryParse(finalLineToken, out lineNumber))
        {
            return false;
        }

        commitId = TrimLeadingCommitMarker(commitToken).ToString();
        return true;
    }

    private static bool TryReadPart(ReadOnlySpan<char> line, int startIndex, out ReadOnlySpan<char> part, out int nextIndex)
    {
        var index = startIndex;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        var partStart = index;
        while (index < line.Length && !char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        part = line[partStart..index];
        nextIndex = index;
        return part.Length > 0;
    }

    private static bool IsCommitToken(ReadOnlySpan<char> token)
    {
        var normalized = TrimLeadingCommitMarker(token);
        if (normalized.Length < 7)
        {
            return false;
        }

        foreach (var character in normalized)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> TrimLeadingCommitMarker(ReadOnlySpan<char> token) =>
        token.Length > 0 && token[0] == '^' ? token[1..] : token;
}
