using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics;

public sealed class BuiltInDiffAnnotationProvider : IDiffAnnotationProvider
{
    public string Id => "semanticdiff.builtin-annotations";

    public ImmutableArray<DiffAnnotation> CreateAnnotations(DiffAnnotationRequest request)
    {
        if (request.Documents.IsDefaultOrEmpty)
        {
            return ImmutableArray<DiffAnnotation>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<DiffAnnotation>();
        var documentsById = request.Documents.ToDictionary(document => document.Id);
        var semanticInsights = new SemanticDocumentInsightIndex()
            .Build(request.SemanticGraph, request.Documents)
            .ToImmutableDictionary(insight => insight.DocumentId);

        AddDocumentAnnotations(request, builder);
        AddLineAnnotations(request.Documents, builder);
        AddSemanticAnnotations(semanticInsights, documentsById, builder);
        AddImpactAnnotations(request.SemanticGraph, documentsById, builder);
        AddNavigationAnnotations(request, documentsById, builder);
        AddReviewCommentAnnotations(request, documentsById, builder);
        AddSelectedDocumentAnnotations(request, documentsById, builder);

        return builder
            .GroupBy(annotation => annotation.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToImmutableArray();
    }

    private static void AddDocumentAnnotations(DiffAnnotationRequest request, ImmutableArray<DiffAnnotation>.Builder builder)
    {
        var firstDocumentId = request.Documents[0].Id;
        foreach (var document in request.Documents)
        {
            builder.Add(DiffAnnotation.Node(
                document.Id,
                DiffAnnotationKind.GitStatus,
                StatusLabel(document.Metadata.Status),
                $"{document.Metadata.Status} {document.Metadata.Path}",
                document.Metadata.Status == DiffFileStatus.Conflicted ? DiffAnnotationSeverity.Error : DiffAnnotationSeverity.Info));

            if (document.Lines.Any(line => !line.Tokens.IsDefaultOrEmpty))
            {
                builder.Add(DiffAnnotation.Node(
                    document.Id,
                    DiffAnnotationKind.Syntax,
                    document.Metadata.Language,
                    $"TextMate syntax colors active for {document.Metadata.Language}",
                    DiffAnnotationSeverity.Hint));
            }
        }

        AddContextNodeAnnotation(request, builder, firstDocumentId, DiffAnnotationKind.ReferenceRange, DiffAnnotationContextKeys.ReferenceRange, "range");
        AddContextNodeAnnotation(request, builder, firstDocumentId, DiffAnnotationKind.RepositoryWatch, DiffAnnotationContextKeys.WatchStatus, "watch");
        AddContextNodeAnnotation(request, builder, firstDocumentId, DiffAnnotationKind.ContextFold, DiffAnnotationContextKeys.DiffContextMode, "context");
    }

    private static void AddLineAnnotations(ImmutableArray<DiffDocumentSnapshot> documents, ImmutableArray<DiffAnnotation>.Builder builder)
    {
        foreach (var document in documents)
        {
            foreach (var line in document.Lines)
            {
                if (!line.InlineSpans.IsDefaultOrEmpty)
                {
                    builder.Add(CreateLineAnnotation(document, line, DiffAnnotationKind.InlineChange, "inline", "Word or character-level changed span", DiffAnnotationSeverity.Info));
                }

                switch (line.Kind)
                {
                    case DiffLineKind.Ignored:
                        builder.Add(CreateLineAnnotation(document, line, DiffAnnotationKind.ReviewNoise, "noise", "Formatter or import-order noise suppressed", DiffAnnotationSeverity.Hint));
                        break;
                    case DiffLineKind.Moved:
                        builder.Add(CreateLineAnnotation(document, line, DiffAnnotationKind.MovedCode, "moved", "Moved or copied code detected", DiffAnnotationSeverity.Info));
                        break;
                    case DiffLineKind.Conflict:
                        builder.Add(CreateLineAnnotation(document, line, DiffAnnotationKind.Conflict, "conflict", "Unresolved merge conflict region", DiffAnnotationSeverity.Error));
                        break;
                    case DiffLineKind.Imaginary:
                        builder.Add(CreateLineAnnotation(document, line, DiffAnnotationKind.ContextFold, "fold", line.Text, DiffAnnotationSeverity.Hint));
                        break;
                }
            }
        }
    }

    private static void AddSemanticAnnotations(
        IReadOnlyDictionary<DiffDocumentId, SemanticDocumentInsight> semanticInsights,
        IReadOnlyDictionary<DiffDocumentId, DiffDocumentSnapshot> documentsById,
        ImmutableArray<DiffAnnotation>.Builder builder)
    {
        if (semanticInsights.Count == 0)
        {
            return;
        }

        foreach (var insight in semanticInsights.Values.Where(insight => insight.HasInsights))
        {
            if (!documentsById.TryGetValue(insight.DocumentId, out var document))
            {
                continue;
            }

            builder.Add(DiffAnnotation.Node(
                document.Id,
                DiffAnnotationKind.SemanticAnchor,
                "sem",
                insight.SummaryText,
                insight.ChangedAnchorCount > 0 || insight.ImpactedEdgeCount > 0 ? DiffAnnotationSeverity.Warning : DiffAnnotationSeverity.Info));

            foreach (var lineInsight in insight.Lines)
            {
                var kind = lineInsight.Kind == SemanticAnchorKind.Unknown
                    ? DiffAnnotationKind.ParserDiagnostic
                    : DiffAnnotationKind.SemanticAnchor;
                var severity = kind == DiffAnnotationKind.ParserDiagnostic
                    ? DiffAnnotationSeverity.Warning
                    : lineInsight.IsChanged || lineInsight.IsImpacted
                        ? DiffAnnotationSeverity.Warning
                        : DiffAnnotationSeverity.Info;
                builder.Add(DiffAnnotation.Line(
                    document.Id,
                    kind,
                    FindLineIndex(document, lineInsight.LineNumber),
                    lineInsight.LineNumber,
                    lineInsight.Label,
                    lineInsight.Detail,
                    severity));
            }
        }
    }

    private static void AddImpactAnnotations(
        SemanticGraph semanticGraph,
        IReadOnlyDictionary<DiffDocumentId, DiffDocumentSnapshot> documentsById,
        ImmutableArray<DiffAnnotation>.Builder builder)
    {
        if (semanticGraph.Anchors.IsDefaultOrEmpty)
        {
            return;
        }

        var changedLinesByDocument = documentsById.Values.ToDictionary(
            document => document.Id,
            document => document.Lines
                .Where(line => line.Kind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified or DiffLineKind.Moved or DiffLineKind.Conflict)
                .SelectMany(line => new[] { line.Index + 1, line.OldLineNumber ?? -1, line.NewLineNumber ?? -1 })
                .Where(lineNumber => lineNumber > 0)
                .ToHashSet());

        foreach (var anchor in semanticGraph.Anchors.Where(anchor => IsImpactAnchor(anchor.Kind)))
        {
            if (!documentsById.TryGetValue(anchor.DocumentId, out var document) ||
                !changedLinesByDocument.TryGetValue(anchor.DocumentId, out var changedLines) ||
                !changedLines.Contains(Math.Max(1, anchor.Range.Line)))
            {
                continue;
            }

            builder.Add(DiffAnnotation.Line(
                document.Id,
                DiffAnnotationKind.Impact,
                FindLineIndex(document, anchor.Range.Line),
                anchor.Range.Line,
                "impact",
                $"Changed semantic context: {anchor.DisplayName}",
                DiffAnnotationSeverity.Warning));
        }
    }

    private static void AddNavigationAnnotations(
        DiffAnnotationRequest request,
        IReadOnlyDictionary<DiffDocumentId, DiffDocumentSnapshot> documentsById,
        ImmutableArray<DiffAnnotation>.Builder builder)
    {
        var documentIdText = request.GetContext(DiffAnnotationContextKeys.CurrentChangeDocumentId);
        var lineIndexText = request.GetContext(DiffAnnotationContextKeys.CurrentChangeLineIndex);
        if (string.IsNullOrWhiteSpace(documentIdText) || !int.TryParse(lineIndexText, out var lineIndex))
        {
            return;
        }

        var documentId = new DiffDocumentId(documentIdText);
        if (!documentsById.TryGetValue(documentId, out var document) || document.Lines.IsDefaultOrEmpty)
        {
            return;
        }

        var line = document.Lines[Math.Clamp(lineIndex, 0, document.Lines.Length - 1)];
        builder.Add(new DiffAnnotation(
            $"{document.Id}:line:{line.Index}:{DiffAnnotationKind.Navigation}:focus",
            document.Id,
            DiffAnnotationKind.Navigation,
            DiffAnnotationTarget.Line,
            line.Index,
            line.NewLineNumber ?? line.OldLineNumber ?? line.Index + 1,
            "focus",
            request.GetContext(DiffAnnotationContextKeys.CurrentChangeText) ?? "Current change navigation target",
            DiffAnnotationSeverity.Info,
            DiffAnnotationActionKind.ChangeNavigation));
    }

    private static void AddSelectedDocumentAnnotations(
        DiffAnnotationRequest request,
        IReadOnlyDictionary<DiffDocumentId, DiffDocumentSnapshot> documentsById,
        ImmutableArray<DiffAnnotation>.Builder builder)
    {
        var selectedDocumentId = request.GetContext(DiffAnnotationContextKeys.SelectedDocumentId);
        if (string.IsNullOrWhiteSpace(selectedDocumentId) || !documentsById.ContainsKey(new DiffDocumentId(selectedDocumentId)))
        {
            return;
        }

        var documentId = new DiffDocumentId(selectedDocumentId);
        AddContextNodeAnnotation(request, builder, documentId, DiffAnnotationKind.HistoryBlame, DiffAnnotationContextKeys.BlameSummary, "blame");
        AddContextNodeAnnotation(request, builder, documentId, DiffAnnotationKind.ReviewAction, DiffAnnotationContextKeys.ReviewActionStatus, "review");
    }

    private static void AddReviewCommentAnnotations(
        DiffAnnotationRequest request,
        IReadOnlyDictionary<DiffDocumentId, DiffDocumentSnapshot> documentsById,
        ImmutableArray<DiffAnnotation>.Builder builder)
    {
        if (request.ReviewThreads.IsDefaultOrEmpty)
        {
            return;
        }

        var documentsByPath = new Dictionary<string, DiffDocumentSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documentsById.Values)
        {
            AddDocumentPath(document.Metadata.Path, document);
            if (!string.IsNullOrWhiteSpace(document.Metadata.OldPath))
            {
                AddDocumentPath(document.Metadata.OldPath, document);
            }
        }

        foreach (var thread in request.ReviewThreads)
        {
            if (string.IsNullOrWhiteSpace(thread.Path) ||
                !documentsByPath.TryGetValue(NormalizePath(thread.Path), out var document))
            {
                continue;
            }

            var detail = FormatReviewThreadDetail(thread);
            var severity = thread.IsResolved ? DiffAnnotationSeverity.Hint : DiffAnnotationSeverity.Warning;
            if (thread.Line is int lineNumber && lineNumber > 0)
            {
                builder.Add(new DiffAnnotation(
                    $"{document.Id}:review-comment:{thread.Id}",
                    document.Id,
                    DiffAnnotationKind.ReviewComment,
                    DiffAnnotationTarget.Line,
                    FindLineIndex(document, lineNumber),
                    lineNumber,
                    thread.IsResolved ? "resolved" : "comment",
                    detail,
                    severity,
                    DiffAnnotationActionKind.ReviewThread,
                    thread.Id));
            }
            else
            {
                builder.Add(new DiffAnnotation(
                    $"{document.Id}:review-comment:{thread.Id}",
                    document.Id,
                    DiffAnnotationKind.ReviewComment,
                    DiffAnnotationTarget.Node,
                    null,
                    null,
                    thread.IsResolved ? "resolved" : "comment",
                    detail,
                    severity,
                    DiffAnnotationActionKind.ReviewThread,
                    thread.Id));
            }
        }

        void AddDocumentPath(string? path, DiffDocumentSnapshot document)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            documentsByPath.TryAdd(NormalizePath(path), document);
        }
    }

    private static DiffAnnotation CreateLineAnnotation(
        DiffDocumentSnapshot document,
        DiffLine line,
        DiffAnnotationKind kind,
        string label,
        string detail,
        DiffAnnotationSeverity severity) => DiffAnnotation.Line(
            document.Id,
            kind,
            line.Index,
            line.NewLineNumber ?? line.OldLineNumber ?? line.Index + 1,
            label,
            detail,
            severity);

    private static void AddContextNodeAnnotation(
        DiffAnnotationRequest request,
        ImmutableArray<DiffAnnotation>.Builder builder,
        DiffDocumentId documentId,
        DiffAnnotationKind kind,
        string contextKey,
        string label)
    {
        var detail = request.GetContext(contextKey);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            builder.Add(DiffAnnotation.Node(documentId, kind, label, detail, DiffAnnotationSeverity.Info));
        }
    }

    private static int FindLineIndex(DiffDocumentSnapshot document, int displayLineNumber)
    {
        if (document.Lines.IsDefaultOrEmpty)
        {
            return 0;
        }

        var line = document.Lines.FirstOrDefault(line => line.NewLineNumber == displayLineNumber || line.OldLineNumber == displayLineNumber);
        return line is not null ? line.Index : Math.Clamp(displayLineNumber - 1, 0, document.Lines.Length - 1);
    }

    private static string FormatReviewThreadDetail(GitReviewThreadInfo thread)
    {
        var state = thread.IsResolved ? "resolved" : "open";
        var count = thread.CommentCount == 1 ? "1 comment" : $"{thread.CommentCount:N0} comments";
        var authors = thread.Comments
            .Select(comment => comment.Author)
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        var authorText = authors.Length == 0 ? string.Empty : $" by {string.Join(", ", authors)}";
        return $"{state} review thread: {thread.Title} ({count}{authorText})";
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    private static bool IsImpactAnchor(SemanticAnchorKind kind) => kind is
        SemanticAnchorKind.Type or
        SemanticAnchorKind.Member or
        SemanticAnchorKind.XamlRoot or
        SemanticAnchorKind.XamlName or
        SemanticAnchorKind.Resource or
        SemanticAnchorKind.Binding;

    private static string StatusLabel(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Added => "added",
        DiffFileStatus.Deleted => "deleted",
        DiffFileStatus.Renamed => "renamed",
        DiffFileStatus.Untracked => "new",
        DiffFileStatus.Conflicted => "conflict",
        _ => "modified"
    };

    private static string SemanticLabel(SemanticAnchorKind kind) => kind switch
    {
        SemanticAnchorKind.XamlRoot => "xaml",
        SemanticAnchorKind.XamlName => "name",
        SemanticAnchorKind.Resource => "res",
        SemanticAnchorKind.Binding => "bind",
        SemanticAnchorKind.Member => "member",
        SemanticAnchorKind.Type => "type",
        _ => "sem"
    };
}
