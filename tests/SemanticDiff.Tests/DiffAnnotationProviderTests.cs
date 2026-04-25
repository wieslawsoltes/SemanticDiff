using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Semantics;

namespace SemanticDiff.Tests;

public sealed class DiffAnnotationProviderTests
{
    [Fact]
    public void BuiltInProvider_MapsDiffSemanticAndContextSignalsToAnnotations()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("Demo.cs"), "Demo.cs", null, DiffFileStatus.Modified, "C#", 1, 1);
        var document = InlineDiffAnnotator.Annotate(factory.CreateFromUnifiedDiff(metadata, "@@ -1 +1 @@\n-old\n+new"));
        var graph = new SemanticGraph(
            [
                new SemanticAnchor("type", document.Id, new TextRange(0, 3, 1, 1), SemanticAnchorKind.Type, "Demo"),
                new SemanticAnchor("parse", document.Id, new TextRange(0, 3, 2, 1), SemanticAnchorKind.Unknown, "XML parse error: invalid tag")
            ],
            []);
        var context = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
        {
            KeyValuePair.Create(DiffAnnotationContextKeys.ReferenceRange, "range main...HEAD"),
            KeyValuePair.Create(DiffAnnotationContextKeys.WatchStatus, "Watching"),
            KeyValuePair.Create(DiffAnnotationContextKeys.SelectedDocumentId, document.Id.Value),
            KeyValuePair.Create(DiffAnnotationContextKeys.BlameSummary, "Blame Ada 2 | latest Ada 2024-01-01 abc123"),
            KeyValuePair.Create(DiffAnnotationContextKeys.ReviewActionStatus, "Ready"),
            KeyValuePair.Create(DiffAnnotationContextKeys.CurrentChangeDocumentId, document.Id.Value),
            KeyValuePair.Create(DiffAnnotationContextKeys.CurrentChangeLineIndex, "2"),
            KeyValuePair.Create(DiffAnnotationContextKeys.CurrentChangeText, "1/2 Demo.cs:1")
        });
        var provider = new BuiltInDiffAnnotationProvider();

        var annotations = provider.CreateAnnotations(new DiffAnnotationRequest([document], graph, context));
        var kinds = annotations.Select(annotation => annotation.Kind).ToHashSet();

        Assert.Contains(DiffAnnotationKind.GitStatus, kinds);
        Assert.Contains(DiffAnnotationKind.ReferenceRange, kinds);
        Assert.Contains(DiffAnnotationKind.RepositoryWatch, kinds);
        Assert.Contains(DiffAnnotationKind.InlineChange, kinds);
        Assert.Contains(DiffAnnotationKind.SemanticAnchor, kinds);
        Assert.Contains(DiffAnnotationKind.ParserDiagnostic, kinds);
        Assert.Contains(DiffAnnotationKind.Impact, kinds);
        Assert.Contains(DiffAnnotationKind.Navigation, kinds);
        Assert.Contains(DiffAnnotationKind.HistoryBlame, kinds);
        Assert.Contains(DiffAnnotationKind.ReviewAction, kinds);
    }
}