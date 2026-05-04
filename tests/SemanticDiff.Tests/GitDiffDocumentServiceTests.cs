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
            DiffContextMode.ChangedHunks,
            CancellationToken.None);

        Assert.Single(snapshot.Documents);
        Assert.Contains(gitService.UnifiedDiffRequests, request => request.ContextLines == 0);
        Assert.Empty(gitService.FileDiffRequests);
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
            DiffContextMode.FullFileDiff,
            CancellationToken.None);

        Assert.Single(snapshot.Documents);
        Assert.Contains(gitService.UnifiedDiffRequests, request => request.ContextLines == 1_000_000);
        Assert.Empty(gitService.FileDiffRequests);
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
            DiffContextMode.CurrentFile,
            CancellationToken.None);

        var document = Assert.Single(snapshot.Documents);
        Assert.Contains(document.Lines, line => line.Text == "public sealed class CurrentFile");
        Assert.All(document.Lines, line => Assert.Equal(DiffLineKind.Context, line.Kind));
        Assert.Single(gitService.ContentRequests);
    }

    [Fact]
    public async Task LoadDocumentsAsync_IncludesAllGitFilesInSnapshotDocuments()
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
            DiffContextMode.ChangedHunks,
            CancellationToken.None);

        Assert.Equal(5, snapshot.GitSnapshot.Files.Length);
        Assert.Equal(
            ["src/Modified1.cs", "src/Modified2.cs", "src/Modified3.cs", "src/New.cs", "src/Copied.cs"],
            snapshot.Documents.Select(document => document.Metadata.Path).ToArray());
    }

    [Fact]
    public async Task LoadDocumentsAsync_LoadsUnifiedDiffBatchWithoutChangingDocumentOrder()
    {
        var fileChanges = ImmutableArray.Create(
            CreateFileChange("src/Modified.cs", DiffFileStatus.Modified),
            CreateFileChange("src/New.cs", DiffFileStatus.Added),
            CreateFileChange("src/Deleted.cs", DiffFileStatus.Deleted),
            CreateFileChange("src/Renamed.cs", DiffFileStatus.Renamed, "src/Old.cs"),
            CreateFileChange("src/Copied.cs", DiffFileStatus.Copied, "src/Original.cs"));
        var gitService = new FakeGitDiffService(
            fileChanges,
            fileChange => $"@@ -0,0 +1,1 @@\n+{fileChange.Path}\n");
        var documentService = new GitDiffDocumentService(gitService, new DiffDocumentFactory());

        var snapshot = await documentService.LoadDocumentsAsync(
            new GitDiffRequest("/repo", GitDiffScope.Worktree),
            DiffContextMode.ChangedHunks,
            CancellationToken.None);

        Assert.Single(gitService.UnifiedDiffRequests);
        Assert.Empty(gitService.FileDiffRequests);
        Assert.Equal(fileChanges.Select(file => file.Path), snapshot.Documents.Select(document => document.Metadata.Path));
    }

    [Fact]
    public async Task LoadDocumentsAsync_UsesBatchedDiffCountsForMetadata()
    {
        var fileChanges = ImmutableArray.Create(
            CreateFileChange("src/Modified.cs", DiffFileStatus.Modified),
            CreateFileChange("src/Added.cs", DiffFileStatus.Added));
        var gitService = new FakeGitDiffService(
            fileChanges,
            fileChange => fileChange.Path.EndsWith("Modified.cs", StringComparison.Ordinal)
                ? "@@ -1,2 +1,3 @@\n-old\n+new\n+another\n"
                : "@@ -0,0 +1,2 @@\n+first\n+second\n");
        var documentService = new GitDiffDocumentService(gitService, new DiffDocumentFactory());

        var snapshot = await documentService.LoadDocumentsAsync(
            new GitDiffRequest("/repo", GitDiffScope.Worktree),
            DiffContextMode.ChangedHunks,
            CancellationToken.None);

        var modified = Assert.Single(snapshot.Documents, document => document.Metadata.Path == "src/Modified.cs");
        var added = Assert.Single(snapshot.Documents, document => document.Metadata.Path == "src/Added.cs");
        Assert.Equal(2, modified.Metadata.AddedLines);
        Assert.Equal(1, modified.Metadata.DeletedLines);
        Assert.Equal(2, added.Metadata.AddedLines);
        Assert.Equal(0, added.Metadata.DeletedLines);
    }

    [Fact]
    public async Task LoadDocumentsAsync_CountsMarkerPrefixedChangedLinesInBatchedDiffs()
    {
        var fileChanges = ImmutableArray.Create(CreateFileChange("src/Markers.txt", DiffFileStatus.Modified));
        var gitService = new FakeGitDiffService(
            fileChanges,
            _ => "@@ -1 +1 @@\n---old\n+++new\n");
        var documentService = new GitDiffDocumentService(gitService, new DiffDocumentFactory());

        var snapshot = await documentService.LoadDocumentsAsync(
            new GitDiffRequest("/repo", GitDiffScope.Worktree),
            DiffContextMode.ChangedHunks,
            CancellationToken.None);

        var document = Assert.Single(snapshot.Documents);
        Assert.Equal(1, document.Metadata.AddedLines);
        Assert.Equal(1, document.Metadata.DeletedLines);
        Assert.Contains(document.Lines, line => line.Kind == DiffLineKind.Deleted && line.Text == "--old");
        Assert.Contains(document.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "++new");
    }

    private static GitFileChange CreateFileChange(string path, DiffFileStatus status, string? oldPath = null) =>
        new(path, oldPath, status, 0, 0, Path.GetExtension(path).Equals(".xaml", StringComparison.OrdinalIgnoreCase) ? "XAML" : "C#");

    private sealed class FakeGitDiffService : IGitDiffService
    {
        private static readonly GitFileChange DefaultFileChange = new("src/File.cs", null, DiffFileStatus.Modified, 1, 1, "C#");
        private readonly ImmutableArray<GitFileChange> fileChanges;
        private readonly Func<GitFileChange, string> unifiedDiffFactory;
        private readonly Func<GitFileChange, string> fileContentFactory;
        private readonly TimeSpan fileDiffDelay;
        private readonly object requestGate = new();
        private int activeFileDiffRequests;
        private int maxConcurrentFileDiffRequests;

        public FakeGitDiffService(string unifiedDiff, string fileContent = "")
            : this(ImmutableArray.Create(DefaultFileChange), _ => unifiedDiff, _ => fileContent, TimeSpan.Zero)
        {
        }

        public FakeGitDiffService(ImmutableArray<GitFileChange> fileChanges, Func<GitFileChange, string> unifiedDiffFactory)
            : this(fileChanges, unifiedDiffFactory, _ => string.Empty, TimeSpan.Zero)
        {
        }

        public FakeGitDiffService(ImmutableArray<GitFileChange> fileChanges, Func<GitFileChange, string> unifiedDiffFactory, TimeSpan fileDiffDelay)
            : this(fileChanges, unifiedDiffFactory, _ => string.Empty, fileDiffDelay)
        {
        }

        private FakeGitDiffService(
            ImmutableArray<GitFileChange> fileChanges,
            Func<GitFileChange, string> unifiedDiffFactory,
            Func<GitFileChange, string> fileContentFactory,
            TimeSpan fileDiffDelay)
        {
            this.fileChanges = fileChanges;
            this.unifiedDiffFactory = unifiedDiffFactory;
            this.fileContentFactory = fileContentFactory;
            this.fileDiffDelay = fileDiffDelay;
        }

        public List<GitDiffRequest> FileDiffRequests { get; } = [];

        public List<GitDiffRequest> UnifiedDiffRequests { get; } = [];

        public List<GitDiffRequest> ContentRequests { get; } = [];

        public int MaxConcurrentFileDiffRequests => maxConcurrentFileDiffRequests;

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

        public Task<string> GetUnifiedDiffAsync(GitDiffRequest request, CancellationToken cancellationToken)
        {
            lock (requestGate)
            {
                UnifiedDiffRequests.Add(request);
            }

            var unifiedDiff = string.Concat(fileChanges.Select(fileChange =>
                $"diff --git a/{fileChange.OldPath ?? fileChange.Path} b/{fileChange.Path}\n" +
                $"--- {(fileChange.Status == DiffFileStatus.Added ? "/dev/null" : $"a/{fileChange.OldPath ?? fileChange.Path}")}\n" +
                $"+++ {(fileChange.Status == DiffFileStatus.Deleted ? "/dev/null" : $"b/{fileChange.Path}")}\n" +
                unifiedDiffFactory(fileChange)));
            return Task.FromResult(unifiedDiff);
        }

        public async Task<GitFileDiff> GetFileDiffAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken)
        {
            lock (requestGate)
            {
                FileDiffRequests.Add(request);
            }

            var activeRequests = Interlocked.Increment(ref activeFileDiffRequests);
            UpdateMaxConcurrentFileDiffRequests(activeRequests);
            try
            {
                if (fileDiffDelay > TimeSpan.Zero)
                {
                    await Task.Delay(fileDiffDelay, cancellationToken);
                }

                return new GitFileDiff(fileChange, unifiedDiffFactory(fileChange));
            }
            finally
            {
                Interlocked.Decrement(ref activeFileDiffRequests);
            }
        }

        public Task<string> GetFileContentAsync(GitDiffRequest request, GitFileChange fileChange, CancellationToken cancellationToken)
        {
            lock (requestGate)
            {
                ContentRequests.Add(request);
            }

            return Task.FromResult(fileContentFactory(fileChange));
        }

        private void UpdateMaxConcurrentFileDiffRequests(int activeRequests)
        {
            var currentMax = Volatile.Read(ref maxConcurrentFileDiffRequests);
            while (activeRequests > currentMax)
            {
                var original = Interlocked.CompareExchange(ref maxConcurrentFileDiffRequests, activeRequests, currentMax);
                if (original == currentMax)
                {
                    return;
                }

                currentMax = original;
            }
        }
    }
}
