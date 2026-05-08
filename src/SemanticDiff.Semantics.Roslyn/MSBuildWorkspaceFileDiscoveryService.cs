using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Git;

namespace SemanticDiff.Semantics.Roslyn;

public sealed record MSBuildWorkspaceFileDiscoveryResult(
    string WorkspacePath,
    ImmutableArray<FileExplorerFile> Files);

public sealed class MSBuildWorkspaceFileDiscoveryService
{
    private readonly IGitCommandRunner commandRunner;

    public MSBuildWorkspaceFileDiscoveryService()
        : this(new MSBuildWorkspaceFactory(), new GitCommandRunner())
    {
    }

    public MSBuildWorkspaceFileDiscoveryService(MSBuildWorkspaceFactory workspaceFactory)
        : this(workspaceFactory, new GitCommandRunner())
    {
    }

    public MSBuildWorkspaceFileDiscoveryService(MSBuildWorkspaceFactory workspaceFactory, IGitCommandRunner commandRunner)
    {
        ArgumentNullException.ThrowIfNull(workspaceFactory);
        this.commandRunner = commandRunner;
    }

    public async Task<MSBuildWorkspaceFileDiscoveryResult> LoadFilesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return new MSBuildWorkspaceFileDiscoveryResult(string.Empty, ImmutableArray<FileExplorerFile>.Empty);
        }

        var workspacePath = MSBuildWorkspaceFactory.FindWorkspacePath(repositoryPath) ?? string.Empty;
        var files = await EnumerateRepositoryWorkspaceFilesAsync(repositoryPath, workspacePath, cancellationToken).ConfigureAwait(false);
        files = files
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(file => GetLanguagePriority(file.Language)).First())
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var displayWorkspacePath = string.IsNullOrWhiteSpace(workspacePath) ? repositoryPath : workspacePath;
        return new MSBuildWorkspaceFileDiscoveryResult(displayWorkspacePath, files);
    }

    private async Task<ImmutableArray<FileExplorerFile>> EnumerateRepositoryWorkspaceFilesAsync(
        string repositoryPath,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var gitFiles = await TryEnumerateGitWorkspaceFilesAsync(cancellationToken).ConfigureAwait(false);
        if (!gitFiles.IsDefaultOrEmpty)
        {
            return gitFiles;
        }

        return EnumerateFileSystemWorkspaceFiles(repositoryPath, workspacePath, cancellationToken).ToImmutableArray();

        async Task<ImmutableArray<FileExplorerFile>> TryEnumerateGitWorkspaceFilesAsync(CancellationToken token)
        {
            var result = await commandRunner.RunAsync(
                repositoryPath,
                ["ls-files", "-z", "--cached", "--others", "--exclude-standard"],
                token).ConfigureAwait(false);
            if (!result.Succeeded || string.IsNullOrEmpty(result.StandardOutput))
            {
                return ImmutableArray<FileExplorerFile>.Empty;
            }

            return result.StandardOutput
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(CreateRelativeFile)
                .OfType<FileExplorerFile>()
                .ToImmutableArray();
        }
    }

    private static bool ShouldSkipWorkspaceFile(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".idea", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".vscode", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static FileExplorerFile? TryCreateFile(string repositoryPath, string? path, string language)
    {
        var relativePath = GetRepositoryRelativePath(repositoryPath, path);
        return string.IsNullOrWhiteSpace(relativePath)
            ? null
            : new FileExplorerFile(relativePath, DiffFileStatus.Unchanged, language);
    }

    private static string? GetRepositoryRelativePath(string repositoryPath, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var repositoryRoot = Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(repositoryRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, repositoryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetRelativePath(repositoryRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Trim('/');
    }

    private static string LanguageFromPath(string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".cs" => "C#",
            ".xaml" => "XAML",
            ".axaml" => "AXAML",
            ".xml" => "XML",
            ".json" => "JSON",
            ".md" or ".markdown" => "Markdown",
            ".sln" or ".slnx" => "Solution",
            ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" => "MSBuild",
            "" => "Text",
            _ => extension.TrimStart('.').ToUpperInvariant()
        };
    }

    private static int GetLanguagePriority(string language) => language switch
    {
        "C#" => 0,
        "XAML" => 1,
        "AXAML" => 2,
        "MSBuild" => 3,
        "Solution" => 4,
        _ => 5
    };

    private static IEnumerable<FileExplorerFile> EnumerateFileSystemWorkspaceFiles(
        string repositoryPath,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        if (TryCreateFile(repositoryPath, workspacePath, LanguageFromPath(workspacePath)) is { } workspaceFile)
        {
            yield return workspaceFile;
        }

        foreach (var filePath in Directory.EnumerateFiles(repositoryPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkipWorkspaceFile(filePath))
            {
                continue;
            }

            if (TryCreateFile(repositoryPath, filePath, LanguageFromPath(filePath)) is { } file)
            {
                yield return file;
            }
        }
    }

    private static FileExplorerFile? CreateRelativeFile(string path)
    {
        var normalizedPath = NormalizeRelativePath(path);
        return string.IsNullOrWhiteSpace(normalizedPath) || ShouldSkipWorkspaceFile(normalizedPath)
            ? null
            : new FileExplorerFile(normalizedPath, DiffFileStatus.Unchanged, LanguageFromPath(normalizedPath));
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').Trim('/');
}
