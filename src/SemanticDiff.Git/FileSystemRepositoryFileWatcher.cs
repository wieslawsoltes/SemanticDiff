using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed class FileSystemRepositoryFileWatcherFactory : IRepositoryFileWatcherFactory
{
    public IRepositoryFileWatcher Watch(string repositoryPath, RepositoryFileWatcherOptions options) => new FileSystemRepositoryFileWatcher(repositoryPath, options);
}

public sealed class FileSystemRepositoryFileWatcher : IRepositoryFileWatcher
{
    private readonly RepositoryFileWatcherOptions options;
    private readonly FileSystemWatcher watcher;

    public FileSystemRepositoryFileWatcher(string repositoryPath, RepositoryFileWatcherOptions options)
    {
        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository path '{repositoryPath}' does not exist.");
        }

        RepositoryPath = Path.GetFullPath(repositoryPath);
        this.options = options;
        watcher = new FileSystemWatcher(RepositoryPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime
        };
        watcher.Created += OnCreated;
        watcher.Changed += OnChanged;
        watcher.Deleted += OnDeleted;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;
    }

    public string RepositoryPath { get; }

    public event EventHandler<RepositoryFileChangedEventArgs>? Changed;

    public ValueTask DisposeAsync()
    {
        watcher.EnableRaisingEvents = false;
        watcher.Created -= OnCreated;
        watcher.Changed -= OnChanged;
        watcher.Deleted -= OnDeleted;
        watcher.Renamed -= OnRenamed;
        watcher.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnCreated(object sender, FileSystemEventArgs args) => Publish(args.FullPath, RepositoryFileChangeKind.Created);

    private void OnChanged(object sender, FileSystemEventArgs args) => Publish(args.FullPath, RepositoryFileChangeKind.Changed);

    private void OnDeleted(object sender, FileSystemEventArgs args) => Publish(args.FullPath, RepositoryFileChangeKind.Deleted);

    private void OnRenamed(object sender, RenamedEventArgs args) => Publish(args.FullPath, RepositoryFileChangeKind.Renamed);

    private void Publish(string fullPath, RepositoryFileChangeKind kind)
    {
        if (!RepositoryFileWatcherPathFilter.ShouldPublish(RepositoryPath, fullPath, options))
        {
            return;
        }

        Changed?.Invoke(this, new RepositoryFileChangedEventArgs(fullPath, kind));
    }
}

public static class RepositoryFileWatcherPathFilter
{
    private static readonly string[] ImportantGitMetadataPaths =
    [
        ".git/HEAD",
        ".git/index",
        ".git/packed-refs",
        ".git/MERGE_HEAD",
        ".git/REBASE_HEAD",
        ".git/CHERRY_PICK_HEAD"
    ];

    public static bool ShouldPublish(string repositoryPath, string fullPath, RepositoryFileWatcherOptions options)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(repositoryPath, fullPath).Replace('\\', '/');
        if (relativePath.Length == 0 || relativePath == "." || relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) || relativePath.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            return options.IncludeGitMetadata && IsImportantGitMetadata(relativePath);
        }

        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (options.EffectiveIgnoredDirectoryNames.Contains(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsImportantGitMetadata(string relativePath)
    {
        if (ImportantGitMetadataPaths.Any(path => relativePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return relativePath.StartsWith(".git/refs/", StringComparison.OrdinalIgnoreCase) ||
               relativePath.StartsWith(".git/logs/refs/", StringComparison.OrdinalIgnoreCase);
    }
}
