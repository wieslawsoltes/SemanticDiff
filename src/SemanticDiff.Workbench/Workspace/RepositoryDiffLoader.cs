using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Git;

namespace SemanticDiff.Workbench.Workspace;

public sealed record RepositoryDiffLoadRequest(
    string RepositoryPath,
    GitDiffScope Scope,
    string? BaseRef,
    string? HeadRef,
    DiffContextMode DiffContextMode,
    DiffReviewMode ReviewMode,
    bool CollapseUnchangedContext);

public sealed record RepositoryDiffLoadResult(
    GitDiffRequest Request,
    GitDiffSnapshot GitSnapshot,
    ImmutableArray<DiffDocumentSnapshot> Documents);

public sealed class RepositoryDiffLoader
{
    private readonly IGitDiffDocumentService documentService;

    public RepositoryDiffLoader()
        : this(new GitDiffDocumentService())
    {
    }

    public RepositoryDiffLoader(IGitDiffDocumentService documentService)
    {
        this.documentService = documentService;
    }

    public async Task<RepositoryDiffLoadResult> LoadAsync(RepositoryDiffLoadRequest request, CancellationToken cancellationToken)
    {
        var gitRequest = new GitDiffRequest(request.RepositoryPath, request.Scope, NormalizeRef(request.BaseRef), NormalizeRef(request.HeadRef));
        var snapshot = await documentService.LoadDocumentsAsync(gitRequest, request.DiffContextMode, cancellationToken);
        var documents = PrepareDocuments(snapshot.Documents, request.ReviewMode, request.CollapseUnchangedContext);
        return new RepositoryDiffLoadResult(gitRequest, snapshot.GitSnapshot, documents);
    }

    public static ImmutableArray<DiffDocumentSnapshot> PrepareDocuments(
        ImmutableArray<DiffDocumentSnapshot> documents,
        DiffReviewMode reviewMode,
        bool collapseUnchangedContext)
    {
        var reviewedDocuments = DiffReviewDocumentTransformer.Apply(documents, reviewMode);
        reviewedDocuments = new DiffConflictAnalyzer().Highlight(reviewedDocuments);
        if (collapseUnchangedContext)
        {
            reviewedDocuments = DiffContextFolder.Apply(reviewedDocuments);
        }

        return InlineDiffAnnotator.Annotate(reviewedDocuments);
    }

    private static string? NormalizeRef(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
