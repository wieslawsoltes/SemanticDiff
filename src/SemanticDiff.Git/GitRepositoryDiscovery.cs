using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed class GitRepositoryDiscovery : IGitRepositoryDiscovery
{
    private readonly IGitCommandRunner commandRunner;

    public GitRepositoryDiscovery()
        : this(new GitCommandRunner())
    {
    }

    public GitRepositoryDiscovery(IGitCommandRunner commandRunner)
    {
        this.commandRunner = commandRunner;
    }

    public async Task<string?> DiscoverRootAsync(string startPath, CancellationToken cancellationToken)
    {
        var normalizedStartPath = NormalizeStartPath(startPath);
        var gitResult = await commandRunner.RunAsync(normalizedStartPath, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);

        if (gitResult.Succeeded && !string.IsNullOrWhiteSpace(gitResult.StandardOutput))
        {
            return gitResult.StandardOutput.Trim();
        }

        return DiscoverByWalkingParents(normalizedStartPath);
    }

    private static string NormalizeStartPath(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return Environment.CurrentDirectory;
        }

        if (Directory.Exists(startPath))
        {
            return Path.GetFullPath(startPath);
        }

        var fileInfo = new FileInfo(startPath);
        return fileInfo.Directory?.FullName ?? Environment.CurrentDirectory;
    }

    private static string? DiscoverByWalkingParents(string startPath)
    {
        var directory = new DirectoryInfo(startPath);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}