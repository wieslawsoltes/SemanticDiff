using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class DiffChangeNavigationIndexTests
{
    [Fact]
    public void Build_OrdersChangedLinesAndExcludesIgnoredPlaceholders()
    {
        var document = CreateDocument();
        var index = new DiffChangeNavigationIndex();

        var items = index.Build([document]);

        Assert.Equal(4, items.Length);
        Assert.Equal([DiffLineKind.Added, DiffLineKind.Deleted, DiffLineKind.Moved, DiffLineKind.Conflict], items.Select(item => item.Kind).ToArray());
        Assert.DoesNotContain(items, item => item.Kind is DiffLineKind.Ignored or DiffLineKind.Imaginary);
    }

    [Fact]
    public void GetAdjacentIndex_WrapsForwardAndBackward()
    {
        var items = new DiffChangeNavigationIndex().Build([CreateDocument()]);

        Assert.Equal(0, DiffChangeNavigationIndex.GetAdjacentIndex(items, -1, 1));
        Assert.Equal(3, DiffChangeNavigationIndex.GetAdjacentIndex(items, -1, -1));
        Assert.Equal(0, DiffChangeNavigationIndex.GetAdjacentIndex(items, 3, 1));
        Assert.Equal(3, DiffChangeNavigationIndex.GetAdjacentIndex(items, 0, -1));
    }

    private static DiffDocumentSnapshot CreateDocument()
    {
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("Sample.cs"), "Sample.cs", null, DiffFileStatus.Modified, "C#", 1, 1);
        return new DiffDocumentSnapshot(
            metadata.Id,
            metadata,
            [
                new DiffLine(0, 1, 1, DiffLineKind.Context, "keep", []),
                new DiffLine(1, null, 2, DiffLineKind.Added, "added", []),
                new DiffLine(2, 3, null, DiffLineKind.Deleted, "deleted", []),
                new DiffLine(3, 4, 4, DiffLineKind.Ignored, "noise", []),
                new DiffLine(4, null, null, DiffLineKind.Imaginary, "... 10 unchanged lines collapsed ...", []),
                new DiffLine(5, 5, 5, DiffLineKind.Moved, "moved", []),
                new DiffLine(6, 6, 6, DiffLineKind.Conflict, "<<<<<<< HEAD", [])
            ]);
    }
}