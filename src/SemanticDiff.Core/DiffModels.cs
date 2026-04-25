using System.Collections.Immutable;

namespace SemanticDiff.Core;

public readonly record struct DiffDocumentId(string Value)
{
    public override string ToString() => Value;
}

public enum DiffFileStatus
{
    Unchanged,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    Untracked,
    Conflicted
}

public enum DiffLineKind
{
    Context,
    Added,
    Deleted,
    Modified,
    Ignored,
    Moved,
    Conflict,
    Metadata,
    Imaginary
}

public enum GitDiffScope
{
    Worktree,
    Unstaged,
    Staged,
    Head,
    Branch,
    CommitRange,
    Custom
}

public enum DiffContextMode
{
    ChangedHunks,
    FullFileDiff,
    CurrentFile
}

public enum DiffReviewMode
{
    Precise,
    IgnoreWhitespace
}

public enum DiffInlineKind
{
    Changed,
    Inserted,
    Deleted
}

public sealed record DiffDocumentMetadata(
    DiffDocumentId Id,
    string Path,
    string? OldPath,
    DiffFileStatus Status,
    string Language,
    int AddedLines,
    int DeletedLines);

public sealed record TokenSpan(int StartColumn, int Length, string StyleId);

public sealed record DiffInlineSpan(int StartColumn, int Length, DiffInlineKind Kind);

public sealed record DiffChangeNavigationItem(
    DiffDocumentId DocumentId,
    string Path,
    int LineIndex,
    int? OldLineNumber,
    int? NewLineNumber,
    DiffLineKind Kind)
{
    public int DisplayLineNumber => NewLineNumber ?? OldLineNumber ?? LineIndex + 1;
}

public sealed record DiffLine(
    int Index,
    int? OldLineNumber,
    int? NewLineNumber,
    DiffLineKind Kind,
    string Text,
    ImmutableArray<TokenSpan> Tokens)
{
    public ImmutableArray<DiffInlineSpan> InlineSpans { get; init; } = ImmutableArray<DiffInlineSpan>.Empty;
}

public sealed record DiffDocumentSnapshot(
    DiffDocumentId Id,
    DiffDocumentMetadata Metadata,
    ImmutableArray<DiffLine> Lines)
{
    public int LineCount => Lines.Length;

    public IEnumerable<DiffLine> GetVisibleLines(int firstLineIndex, int lineCount)
    {
        var firstLine = Math.Clamp(firstLineIndex, 0, Math.Max(0, Lines.Length));
        var lastLine = Math.Clamp(firstLine + Math.Max(0, lineCount), 0, Lines.Length);

        for (var lineIndex = firstLine; lineIndex < lastLine; lineIndex++)
        {
            yield return Lines[lineIndex];
        }
    }

    public string ToSourceText() => string.Join(Environment.NewLine, Lines.Select(line => line.Text));
}

public sealed record GitFileChange(
    string Path,
    string? OldPath,
    DiffFileStatus Status,
    int AddedLines,
    int DeletedLines,
    string Language);

public sealed record GitDiffRequest(
    string RepositoryPath,
    GitDiffScope Scope,
    string? BaseRef = null,
    string? HeadRef = null,
    string? PathFilter = null,
    int ContextLines = 0);

public sealed record GitDiffSnapshot(
    string RepositoryPath,
    GitDiffRequest Request,
    string? DefaultBranch,
    ImmutableArray<GitFileChange> Files,
    DateTimeOffset CreatedAt);

public sealed record GitFileDiff(GitFileChange FileChange, string UnifiedDiff);

public sealed record GitDiffDocumentSnapshot(
    GitDiffSnapshot GitSnapshot,
    ImmutableArray<DiffDocumentSnapshot> Documents);