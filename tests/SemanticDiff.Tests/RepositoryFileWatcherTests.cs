using SemanticDiff.Core;
using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class RepositoryFileWatcherTests
{
    [Fact]
    public void PathFilter_AllowsSourceFilesAndImportantGitMetadata()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), "SemanticDiffWatcherFilter");
        var options = new RepositoryFileWatcherOptions();

        Assert.True(RepositoryFileWatcherPathFilter.ShouldPublish(repositoryPath, Path.Combine(repositoryPath, "src", "App.cs"), options));
        Assert.True(RepositoryFileWatcherPathFilter.ShouldPublish(repositoryPath, Path.Combine(repositoryPath, ".git", "index"), options));
        Assert.True(RepositoryFileWatcherPathFilter.ShouldPublish(repositoryPath, Path.Combine(repositoryPath, ".git", "refs", "heads", "main"), options));
        Assert.False(RepositoryFileWatcherPathFilter.ShouldPublish(repositoryPath, Path.Combine(repositoryPath, "bin", "Debug", "App.dll"), options));
        Assert.False(RepositoryFileWatcherPathFilter.ShouldPublish(repositoryPath, Path.Combine(repositoryPath, ".git", "objects", "aa", "bbbb"), options));
    }

    [Fact]
    public async Task FileSystemWatcher_RaisesChangedEventForCreatedFile()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"SemanticDiffWatcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        var filePath = Path.Combine(repositoryPath, "sample.txt");
        var changeCompletion = new TaskCompletionSource<RepositoryFileChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await using var watcher = new FileSystemRepositoryFileWatcher(repositoryPath, new RepositoryFileWatcherOptions());
            watcher.Changed += (_, args) =>
            {
                if (Path.GetFileName(args.FullPath) == "sample.txt")
                {
                    changeCompletion.TrySetResult(args);
                }
            };

            await File.WriteAllTextAsync(filePath, "changed");

            var completedTask = await Task.WhenAny(changeCompletion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(changeCompletion.Task, completedTask);
            var changedEvent = await changeCompletion.Task;
            Assert.Equal(filePath, changedEvent.FullPath);
        }
        finally
        {
            if (Directory.Exists(repositoryPath))
            {
                Directory.Delete(repositoryPath, recursive: true);
            }
        }
    }
}
