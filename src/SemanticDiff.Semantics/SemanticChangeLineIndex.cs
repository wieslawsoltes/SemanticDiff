using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics;

public sealed record SemanticChangedLineIndex(
    ImmutableDictionary<DiffDocumentId, ImmutableHashSet<int>> ChangedLinesByDocumentId,
    int MovedLineCount,
    int IgnoredLineCount)
{
    public static SemanticChangedLineIndex Empty { get; } = new(
        ImmutableDictionary<DiffDocumentId, ImmutableHashSet<int>>.Empty,
        0,
        0);

    public bool Contains(SemanticAnchor anchor) =>
        ChangedLinesByDocumentId.TryGetValue(anchor.DocumentId, out var changedLines) &&
        changedLines.Contains(Math.Max(1, anchor.Range.Line));
}

public sealed class SemanticChangeLineIndexBuilder
{
    public SemanticChangedLineIndex Build(ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return SemanticChangedLineIndex.Empty;
        }

        var changedLinesByDocumentId = ImmutableDictionary.CreateBuilder<DiffDocumentId, ImmutableHashSet<int>>();
        var movedLineCount = 0;
        var ignoredLineCount = 0;

        foreach (var document in documents)
        {
            var changedLines = ImmutableHashSet.CreateBuilder<int>();
            foreach (var line in document.Lines)
            {
                if (line.Kind == DiffLineKind.Moved)
                {
                    movedLineCount++;
                }
                else if (line.Kind == DiffLineKind.Ignored)
                {
                    ignoredLineCount++;
                    continue;
                }

                if (!IsImpactingLine(line.Kind))
                {
                    continue;
                }

                changedLines.Add(line.Index + 1);
                if (line.OldLineNumber is { } oldLineNumber)
                {
                    changedLines.Add(oldLineNumber);
                }

                if (line.NewLineNumber is { } newLineNumber)
                {
                    changedLines.Add(newLineNumber);
                }
            }

            if (changedLines.Count > 0)
            {
                changedLinesByDocumentId[document.Id] = changedLines.ToImmutable();
            }
        }

        return new SemanticChangedLineIndex(changedLinesByDocumentId.ToImmutable(), movedLineCount, ignoredLineCount);
    }

    private static bool IsImpactingLine(DiffLineKind kind) => kind is
        DiffLineKind.Added or
        DiffLineKind.Deleted or
        DiffLineKind.Modified or
        DiffLineKind.Moved or
        DiffLineKind.Conflict;
}
