using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class DiffConflictAnalyzerTests
{
    [Fact]
    public void Highlight_MarksConflictMarkerRegion()
    {
        var document = CreateTextDocument(DiffFileStatus.Modified,
            "before\n<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> branch\nafter");
        var analyzer = new DiffConflictAnalyzer();

        var highlighted = analyzer.Highlight(document);

        Assert.Equal(5, highlighted.Lines.Count(line => line.Kind == DiffLineKind.Conflict));
        Assert.Equal(DiffLineKind.Context, highlighted.Lines[0].Kind);
        Assert.Equal(DiffLineKind.Context, highlighted.Lines[^1].Kind);
    }

    [Fact]
    public void Analyze_CountsMarkerRegionsAndConflictedStatusFiles()
    {
        var withMarkers = CreateTextDocument(DiffFileStatus.Modified,
            "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> branch");
        var conflictedWithoutMarkers = CreateTextDocument(DiffFileStatus.Conflicted, "resolved-looking text");
        var analyzer = new DiffConflictAnalyzer();

        var summary = analyzer.Analyze([withMarkers, conflictedWithoutMarkers]);

        Assert.Equal(2, summary.ConflictedFileCount);
        Assert.Equal(1, summary.ConflictRegionCount);
    }

    [Fact]
    public void FindRegions_HandlesUnterminatedConflictMarkers()
    {
        var document = CreateTextDocument(DiffFileStatus.Modified, "before\n<<<<<<< HEAD\nours");
        var analyzer = new DiffConflictAnalyzer();

        var region = Assert.Single(analyzer.FindRegions(document));

        Assert.Equal(1, region.StartLineIndex);
        Assert.Equal(2, region.EndLineIndex);
    }

    private static DiffDocumentSnapshot CreateTextDocument(DiffFileStatus status, string text)
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("Sample.cs"), "Sample.cs", null, status, "C#", 0, 0);
        return factory.CreateFromText(metadata, text);
    }
}