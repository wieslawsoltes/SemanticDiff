using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class InlineDiffAnnotatorTests
{
    [Fact]
    public void Annotate_MarksSingleChangedWordOnDeletedAndAddedLines()
    {
        var document = CreateDocument(
            """
            @@ -1 +1 @@
            -return oldValue;
            +return newValue;
            """);

        var annotated = InlineDiffAnnotator.Annotate(document);

        var deletedLine = annotated.Lines.Single(line => line.Kind == DiffLineKind.Deleted);
        var addedLine = annotated.Lines.Single(line => line.Kind == DiffLineKind.Added);
        Assert.Equal("oldValue", ReadSpanText(deletedLine, Assert.Single(deletedLine.InlineSpans)));
        Assert.Equal("newValue", ReadSpanText(addedLine, Assert.Single(addedLine.InlineSpans)));
    }

    [Fact]
    public void Annotate_MarksMultipleChangedSegmentsWithinALine()
    {
        var document = CreateDocument(
            """
            @@ -1 +1 @@
            -Call(first, oldValue, last);
            +Call(second, newValue, last);
            """);

        var annotated = InlineDiffAnnotator.Annotate(document);

        var addedLine = annotated.Lines.Single(line => line.Kind == DiffLineKind.Added);
        var changedText = addedLine.InlineSpans.Select(span => ReadSpanText(addedLine, span)).ToArray();
        Assert.Contains("second", changedText);
        Assert.Contains("newValue", changedText);
    }

    [Fact]
    public void Annotate_DoesNotAnnotateIgnoredNoiseLines()
    {
        var document = CreateDocument(
            """
            @@ -1 +1,3 @@
            -Call(value, other);
            +Call(
            +    value,
            +    other);
            """);
        var reviewed = DiffReviewDocumentTransformer.Apply(document, DiffReviewMode.IgnoreWhitespace);

        var annotated = InlineDiffAnnotator.Annotate(reviewed);

        Assert.DoesNotContain(annotated.Lines, line => !line.InlineSpans.IsDefaultOrEmpty);
    }

    [Fact]
    public void Annotate_UsesFallbackSpansForVeryFragmentedLines()
    {
        var deletedText = "x" + new string('!', 600) + "a";
        var addedText = "x" + new string('?', 600) + "a";
        var document = CreateDocument($"@@ -1 +1 @@\n-{deletedText}\n+{addedText}\n");

        var annotated = InlineDiffAnnotator.Annotate(document);

        var deletedLine = annotated.Lines.Single(line => line.Kind == DiffLineKind.Deleted);
        var addedLine = annotated.Lines.Single(line => line.Kind == DiffLineKind.Added);
        var deletedSpan = Assert.Single(deletedLine.InlineSpans);
        var addedSpan = Assert.Single(addedLine.InlineSpans);
        Assert.Equal(1, deletedSpan.StartColumn);
        Assert.Equal(600, deletedSpan.Length);
        Assert.Equal(1, addedSpan.StartColumn);
        Assert.Equal(600, addedSpan.Length);
    }

    private static DiffDocumentSnapshot CreateDocument(string unifiedDiff)
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("Sample.cs"), "Sample.cs", null, DiffFileStatus.Modified, "C#", 0, 0);
        return factory.CreateFromUnifiedDiff(metadata, unifiedDiff);
    }

    private static string ReadSpanText(DiffLine line, DiffInlineSpan span) => line.Text.Substring(span.StartColumn, span.Length);
}
