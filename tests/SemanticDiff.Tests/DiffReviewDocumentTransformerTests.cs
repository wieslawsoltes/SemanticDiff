using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class DiffReviewDocumentTransformerTests
{
    [Fact]
    public void Apply_IgnoreWhitespace_MarksSplitJoinFormattingAsIgnored()
    {
        var document = CreateDocument(
            """
            @@ -1 +1,3 @@
            -Call(value, other);
            +Call(
            +    value,
            +    other);
            """);

        var transformed = DiffReviewDocumentTransformer.Apply(document, DiffReviewMode.IgnoreWhitespace);

        Assert.Equal(4, transformed.Lines.Count(line => line.Kind == DiffLineKind.Ignored));
        Assert.DoesNotContain(transformed.Lines, line => line.Kind is DiffLineKind.Added or DiffLineKind.Deleted);
    }

    [Fact]
    public void Apply_IgnoreWhitespace_MarksImportOrderOnlyChangesAsIgnored()
    {
        var document = CreateDocument(
            """
            @@ -1,2 +1,2 @@
            -using Sample.B;
             using Sample.A;
            +using Sample.B;
            """);

        var transformed = DiffReviewDocumentTransformer.Apply(document, DiffReviewMode.IgnoreWhitespace);

        Assert.Equal(2, transformed.Lines.Count(line => line.Kind == DiffLineKind.Ignored));
    }

    [Fact]
    public void Apply_Precise_MarksExactMovedLines()
    {
        var document = CreateDocument(
            """
            @@ -1,3 +1,3 @@
            +return CalculateTotal();
             var total = 42;
            -return CalculateTotal();
            """);

        var transformed = DiffReviewDocumentTransformer.Apply(document, DiffReviewMode.Precise);

        Assert.Equal(2, transformed.Lines.Count(line => line.Kind == DiffLineKind.Moved));
    }

    private static DiffDocumentSnapshot CreateDocument(string unifiedDiff)
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("Sample.cs"), "Sample.cs", null, DiffFileStatus.Modified, "C#", 0, 0);
        return factory.CreateFromUnifiedDiff(metadata, unifiedDiff);
    }
}