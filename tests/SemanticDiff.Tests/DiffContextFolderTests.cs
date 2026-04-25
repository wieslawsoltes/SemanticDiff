using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class DiffContextFolderTests
{
    [Fact]
    public void Apply_FoldsLongContextRunAroundChanges()
    {
        var document = CreateDocument(
            "@@ -1,17 +1,17 @@\n" +
            string.Join("\n", Enumerable.Range(1, 12).Select(line => $" context {line}")) +
            "\n-old\n+new\n" +
            string.Join("\n", Enumerable.Range(13, 12).Select(line => $" context {line}")));

        var folded = DiffContextFolder.Apply(document, visibleContextLines: 2, minimumFoldLineCount: 4);

        Assert.Contains(folded.Lines, line => line.Kind == DiffLineKind.Imaginary && line.Text.Contains("8 unchanged", StringComparison.Ordinal));
        Assert.Contains(folded.Lines, line => line.Kind == DiffLineKind.Deleted && line.Text == "old");
        Assert.Contains(folded.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "new");
        Assert.Equal(Enumerable.Range(0, folded.LineCount), folded.Lines.Select(line => line.Index));
    }

    [Fact]
    public void Apply_LeavesDocumentsWithoutChangesUnfolded()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("Sample.cs"), "Sample.cs", null, DiffFileStatus.Modified, "C#", 0, 0);
        var document = factory.CreateFromText(metadata, string.Join('\n', Enumerable.Range(1, 20).Select(line => $"line {line}")));

        var folded = DiffContextFolder.Apply(document, visibleContextLines: 2, minimumFoldLineCount: 4);

        Assert.Equal(document.LineCount, folded.LineCount);
        Assert.DoesNotContain(folded.Lines, line => line.Kind == DiffLineKind.Imaginary);
    }

    private static DiffDocumentSnapshot CreateDocument(string unifiedDiff)
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("Sample.cs"), "Sample.cs", null, DiffFileStatus.Modified, "C#", 1, 1);
        return factory.CreateFromUnifiedDiff(metadata, unifiedDiff);
    }
}