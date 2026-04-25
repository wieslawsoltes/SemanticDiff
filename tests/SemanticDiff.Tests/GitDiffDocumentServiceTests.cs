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

    [Fact]
    public async Task LoadDocumentsAsync_PrioritizesStructuralChangesWhenMaxFilesCapsInitialLoad()
    {
        var fileChanges = ImmutableArray.Create(
            CreateFileChange("src/Modified1.cs", DiffFileStatus.Modified),
            CreateFileChange("src/Modified2.cs", DiffFileStatus.Modified),
            CreateFileChange("src/Modified3.cs", DiffFileStatus.Modified),
            CreateFileChange("src/New.cs", DiffFileStatus.Added),
            CreateFileChange("src/Copied.cs", DiffFileStatus.Copied, "src/Original.cs"));
        var gitService = new FakeGitDiffService(
            fileChanges,
            fileChange => $"@@ -0,0 +1,1 @@\n+{fileChange.Path}\n");
        var documentService = new GitDiffDocumentService(gitService, new DiffDocumentFactory());

        var snapshot = await documentService.LoadDocumentsAsync(
            new GitDiffRequest("/repo", GitDiffScope.Branch),
            2,
            DiffContextMode.ChangedHunks,
            CancellationToken.None);

        Assert.Equal(5, snapshot.GitSnapshot.Files.Length);
        Assert.Equal(["src/New.cs", "src/Copied.cs"], snapshot.Documents.Select(document => document.Metadata.Path).ToArray());
    }

    private static GitFileChange CreateFileChange(string path, DiffFileStatus status, string? oldPath = null) =>
        new(path, oldPath, status, 0, 0, Path.GetExtension(path).Equals(".xaml", StringComparison.OrdinalIgnoreCase) ? "XAML" : "C#");

    private sealed class FakeGitDiffService : IGitDiffService
    {
        private static readonly GitFileChange DefaultFileChange = new("src/File.cs", null, DiffFileStatus.Modified, 1, 1, "C#");
        private readonly ImmutableArray<GitFileChange> fileChanges;
        private readonly Func<GitFileChange, string> unifiedDiffFactory;
        private readonly Func<GitFileChange, string> fileContentFactory;

        public FakeGitDiffService(string unifiedDiff, string fileContent = "")
            : this(ImmutableArray.Create(DefaultFileChange), _ => unifiedDiff, _ => fileContent)
        {
        }

        public FakeGitDiffService(ImmutableArray<GitFileChange> fileChanges, Func<GitFileChange, string> unifiedDiffFactory)
            : this(fileChanges, unifiedDiffFactory, _ => string.Empty)
        {
        }

        private FakeGitDiffService(
            ImmutableArray<GitFileChange> fileChanges,
            Func<GitFileChange, string> unifiedDiffFactory,
            Func<GitFileChange, string> fileContentFactory)
        {
            this.fileChanges = fileChanges;
            this.unifiedDiffFactory = unifiedDiffFactory;
            this.fileContentFactory = fileContentFactory;
        }

        public List<GitDiffRequest> FileDiffRequests { get; } = [];

        public List<GitDiffRequest> ContentRequests { get; } = [];

        public Task<GitDiffSnapshot> GetDiffAsync(GitDiffRequest request, CancellationToken cancellationToken)
        {
            var snapshot = new GitDiffSnapshot(
                request.RepositoryPath,
                request,
                "origin/main",
                fileChanges,
                DateTimeOffset.UtcNow);
            return Task.FromResult(snapshot);
        }

        public Task<GitFileDiff> GetFileDiffAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken)
        {
            FileDiffRequests.Add(request);
            return Task.FromResult(new GitFileDiff(fileChange, unifiedDiffFactory(fileChange)));
        }

        public Task<string> GetFileContentAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken)
        {
            ContentRequests.Add(request);
            return Task.FromResult(fileContentFactory(fileChange));
        }
    }
}
