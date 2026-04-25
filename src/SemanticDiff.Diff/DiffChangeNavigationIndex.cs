using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public sealed class DiffChangeNavigationIndex
{
    public ImmutableArray<DiffChangeNavigationItem> Build(ImmutableArray<DiffDocumentSnapshot> documents)
    {
        if (documents.IsDefaultOrEmpty)
        {
            return ImmutableArray<DiffChangeNavigationItem>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<DiffChangeNavigationItem>();
        foreach (var document in documents)
        {
            foreach (var line in document.Lines)
            {
                if (!IsNavigableChange(line))
                {
                    continue;
                }

                builder.Add(new DiffChangeNavigationItem(
                    document.Id,
                    document.Metadata.Path,
                    line.Index,
                    line.OldLineNumber,
                    line.NewLineNumber,
                    line.Kind));
            }
        }

        return builder.ToImmutable();
    }

    public static int GetAdjacentIndex(ImmutableArray<DiffChangeNavigationItem> items, int currentIndex, int direction)
    {
        if (items.IsDefaultOrEmpty)
        {
            return -1;
        }

        var normalizedDirection = direction < 0 ? -1 : 1;
        if (currentIndex < 0 || currentIndex >= items.Length)
        {
            return normalizedDirection > 0 ? 0 : items.Length - 1;
        }

        return (currentIndex + normalizedDirection + items.Length) % items.Length;
    }

    private static bool IsNavigableChange(DiffLine line) => line.Kind is
        DiffLineKind.Added or
        DiffLineKind.Deleted or
        DiffLineKind.Modified or
        DiffLineKind.Moved or
        DiffLineKind.Conflict;
}