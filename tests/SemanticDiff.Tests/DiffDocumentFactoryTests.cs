using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class DiffDocumentFactoryTests
{
    [Fact]
    public void CreateFromUnifiedDiff_PreservesLineKinds()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("sample.cs"), "sample.cs", null, DiffFileStatus.Modified, "C#", 1, 1);

        var document = factory.CreateFromUnifiedDiff(metadata, """
            @@ -1,2 +1,2 @@
             keep
            -old
            +new
            """);

        Assert.Contains(document.Lines, line => line.Kind == DiffLineKind.Deleted && line.Text == "old");
        Assert.Contains(document.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "new");
    }

    [Fact]
    public void CreateFromText_TreatsAllNewLineFormsAsLineBreaks()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("sample.txt"), "sample.txt", null, DiffFileStatus.Modified, "Text", 0, 0);

        var document = factory.CreateFromText(metadata, "one\rtwo\r\nthree\n");

        Assert.Equal(["one", "two", "three", ""], document.Lines.Select(line => line.Text).ToArray());
    }

    [Fact]
    public void CreateFromUnifiedDiff_TreatsMarkerPrefixedContentAsChangedLines()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("sample.txt"), "sample.txt", null, DiffFileStatus.Modified, "Text", 0, 0);

        var document = factory.CreateFromUnifiedDiff(metadata, """
            --- a/sample.txt
            +++ b/sample.txt
            @@ -1 +1 @@
            ---old
            +++new
            """);

        Assert.Contains(document.Lines, line => line.Kind == DiffLineKind.Deleted && line.Text == "--old");
        Assert.Contains(document.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "++new");
    }

    [Fact]
    public void CreateFromUnifiedDiff_TreatsNoNewlineMarkersAsMetadata()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("sample.txt"), "sample.txt", null, DiffFileStatus.Modified, "Text", 0, 0);

        var document = factory.CreateFromUnifiedDiff(metadata, """
            @@ -1 +1 @@
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file
            """);

        var markers = document.Lines
            .Where(line => line.Text == @"\ No newline at end of file")
            .ToArray();
        Assert.Equal(2, markers.Length);
        Assert.All(markers, line =>
        {
            Assert.Equal(DiffLineKind.Metadata, line.Kind);
            Assert.Null(line.OldLineNumber);
            Assert.Null(line.NewLineNumber);
        });
        Assert.Contains(document.Lines, line => line.Kind == DiffLineKind.Added && line.NewLineNumber == 1);
    }
}
