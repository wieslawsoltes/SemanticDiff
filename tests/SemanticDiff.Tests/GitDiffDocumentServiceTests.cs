using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitDiffDocumentServiceTests
{
    [Fact]
    public async Task LoadDocumentsAsync_UsesZeroContextForChangedHunksMode()
    {
        var gitService = new FakeGitDiffService(
            """
            @@ -1 +1 @@
            -old
            +new
            """);
        var documentService = new GitDiffDocumentService(gitService, new DiffDocumentFactory());

        var snapshot = await documentService.LoadDocumentsAsync(
            new GitDiffRequest("/repo", GitDiffScope.Worktree),
            4,
            DiffContextMode.ChangedHunks,
            CancellationToken.None);

        Assert.Single(snapshot.Documents);
        Assert.Contains(gitService.FileDiffRequests, request => request.ContextLines == 0);
    }

    [Fact]
    public async Task LoadDocumentsAsync_RequestsFullContextForFullFileDiffMode()
    {
        var gitService = new FakeGitDiffService(
            """
            @@ -1,2 +1,2 @@
             keep
            -old
            +new
            """);
        var documentService = new GitDiffDocumentService(gitService, new DiffDocumentFactory());

        var snapshot = await documentService.LoadDocumentsAsync(
            new GitDiffRequest("/repo", GitDiffScope.Worktree),
            4,
            DiffContextMode.FullFileDiff,
            CancellationToken.None);

        Assert.Single(snapshot.Documents);
        Assert.Contains(gitService.FileDiffRequests, request => request.ContextLines == 1_000_000);
    }

    [Fact]
    public async Task LoadDocumentsAsync_UsesCurrentFileContentForCurrentFileMode()
    {
        var gitService = new FakeGitDiffService(
            """
            @@ -1,1 +1,1 @@
            -old
            +new
            """,
            "public sealed class CurrentFile\n{\n}\n");
        var documentService = new GitDiffDocumentService(gitService, new DiffDocumentFactory());

        var snapshot = await documentService.LoadDocumentsAsync(
            new GitDiffRequest("/repo", GitDiffScope.Worktree),
            4,
            DiffContextMode.CurrentFile,
            CancellationToken.None);

        var document = Assert.Single(snapshot.Documents);
        Assert.Contains(document.Lines, line => line.Text == "public sealed class CurrentFile");
        Assert.All(document.Lines, line => Assert.Equal(DiffLineKind.Context, line.Kind));
        Assert.Single(gitService.ContentRequests);
    }

    private sealed class FakeGitDiffService : IGitDiffService
    {
        private readonly string unifiedDiff;
        private readonly string fileContent;
        private readonly GitFileChange fileChange = new("src/File.cs", null, DiffFileStatus.Modified, 1, 1, "C#");

        public FakeGitDiffService(string unifiedDiff, string fileContent = "")
        {
            this.unifiedDiff = unifiedDiff;
            this.fileContent = fileContent;
        }

        public List<GitDiffRequest> FileDiffRequests { get; } = [];

        public List<GitDiffRequest> ContentRequests { get; } = [];

        public Task<GitDiffSnapshot> GetDiffAsync(GitDiffRequest request, CancellationToken cancellationToken)
        {
            var snapshot = new GitDiffSnapshot(
                request.RepositoryPath,
                request,
                "origin/main",
                ImmutableArray.Create(fileChange),
                DateTimeOffset.UtcNow);
            return Task.FromResult(snapshot);
        }

        public Task<GitFileDiff> GetFileDiffAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken)
        {
            FileDiffRequests.Add(request);
            return Task.FromResult(new GitFileDiff(fileChange, unifiedDiff));
        }

        public Task<string> GetFileContentAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken)
        {
            ContentRequests.Add(request);
            return Task.FromResult(fileContent);
        }
    }
}
