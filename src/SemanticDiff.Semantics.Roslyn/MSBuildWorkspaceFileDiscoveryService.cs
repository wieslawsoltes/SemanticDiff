using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics.Roslyn;

public sealed record MSBuildWorkspaceFileDiscoveryResult(
    string WorkspacePath,
    ImmutableArray<FileExplorerFile> Files);

public sealed class MSBuildWorkspaceFileDiscoveryService
{
    private readonly MSBuildWorkspaceFactory workspaceFactory;

    public MSBuildWorkspaceFileDiscoveryService()
        : this(new MSBuildWorkspaceFactory())
    {
    }

    public MSBuildWorkspaceFileDiscoveryService(MSBuildWorkspaceFactory workspaceFactory)
    {
        this.workspaceFactory = workspaceFactory;
    }

    public async Task<MSBuildWorkspaceFileDiscoveryResult> LoadFilesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return new MSBuildWorkspaceFileDiscoveryResult(string.Empty, ImmutableArray<FileExplorerFile>.Empty);
        }

        var workspacePath = MSBuildWorkspaceFactory.FindWorkspacePath(repositoryPath);
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return new MSBuildWorkspaceFileDiscoveryResult(string.Empty, ImmutableArray<FileExplorerFile>.Empty);
        }

        using var workspace = workspaceFactory.CreateWorkspace();
        var projectFallbackPaths = MSBuildWorkspaceFactory.FindProjectPaths(repositoryPath);
        var loadedWorkspace = await LoadWorkspaceSolutionAsync(workspace, workspacePath, projectFallbackPaths, cancellationToken).ConfigureAwait(false);

        var files = EnumerateWorkspaceFiles(repositoryPath, workspacePath, loadedWorkspace.Solution, loadedWorkspace.ProjectFallbackPaths)
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(file => GetLanguagePriority(file.Language)).First())
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new MSBuildWorkspaceFileDiscoveryResult(workspacePath, files);
    }

    private static async Task<LoadedWorkspaceSolution> LoadWorkspaceSolutionAsync(
        Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace workspace,
        string workspacePath,
        ImmutableArray<string> projectFallbackPaths,
        CancellationToken cancellationToken)
    {
        if (!IsProjectFile(workspacePath))
        {
            try
            {
                var solution = await workspace.OpenSolutionAsync(workspacePath, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new LoadedWorkspaceSolution(solution, ImmutableArray<string>.Empty);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
            {
                return await LoadProjectFallbackSolutionAsync(workspace, workspacePath, projectFallbackPaths, cancellationToken).ConfigureAwait(false);
            }
        }

        return await LoadProjectFallbackSolutionAsync(workspace, workspacePath, projectFallbackPaths, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LoadedWorkspaceSolution> LoadProjectFallbackSolutionAsync(
        Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace workspace,
        string workspacePath,
        ImmutableArray<string> projectFallbackPaths,
        CancellationToken cancellationToken)
    {
        var fallbackPaths = projectFallbackPaths.IsDefaultOrEmpty && IsProjectFile(workspacePath)
            ? ImmutableArray.Create(workspacePath)
            : projectFallbackPaths;
        IEnumerable<string> projectPaths = fallbackPaths;
        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
            {
                // A single unsupported project should not collapse the whole workspace file explorer.
            }
        }

        return new LoadedWorkspaceSolution(
            workspace.CurrentSolution,
            fallbackPaths);
    }

    private static IEnumerable<FileExplorerFile> EnumerateWorkspaceFiles(
        string repositoryPath,
        string workspacePath,
        Solution solution,
        ImmutableArray<string> projectFallbackPaths)
    {
        if (TryCreateFile(repositoryPath, workspacePath, LanguageFromPath(workspacePath)) is { } workspaceFile)
        {
            yield return workspaceFile;
        }

        var fallbackProjectPaths = projectFallbackPaths.IsDefaultOrEmpty
            ? ImmutableArray<string>.Empty
            : projectFallbackPaths;
        foreach (var projectPath in fallbackProjectPaths)
        {
            if (TryCreateFile(repositoryPath, projectPath, LanguageFromPath(projectPath)) is { } projectFile)
            {
                yield return projectFile;
            }

            foreach (var projectDirectoryFile in EnumerateProjectDirectoryFiles(repositoryPath, projectPath))
            {
                yield return projectDirectoryFile;
            }
        }

        foreach (var project in solution.Projects)
        {
            if (TryCreateFile(repositoryPath, project.FilePath, LanguageFromPath(project.FilePath)) is { } projectFile)
            {
                yield return projectFile;
            }

            foreach (var projectDirectoryFile in EnumerateProjectDirectoryFiles(repositoryPath, project.FilePath))
            {
                yield return projectDirectoryFile;
            }

            foreach (var document in project.Documents)
            {
                if (TryCreateFile(repositoryPath, document.FilePath, LanguageFromProject(project.Language, document.FilePath)) is { } file)
                {
                    yield return file;
                }
            }

            foreach (var document in project.AdditionalDocuments)
            {
                if (TryCreateFile(repositoryPath, document.FilePath, LanguageFromPath(document.FilePath)) is { } file)
                {
                    yield return file;
                }
            }

            foreach (var document in project.AnalyzerConfigDocuments)
            {
                if (TryCreateFile(repositoryPath, document.FilePath, LanguageFromPath(document.FilePath)) is { } file)
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<FileExplorerFile> EnumerateProjectDirectoryFiles(string repositoryPath, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            yield break;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            yield break;
        }

        foreach (var filePath in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories))
        {
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

    private static bool IsProjectFile(string workspacePath)
    {
        var extension = Path.GetExtension(workspacePath);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string LanguageFromProject(string projectLanguage, string? path) =>
        projectLanguage == LanguageNames.CSharp ? "C#" : LanguageFromPath(path);

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

    private sealed record LoadedWorkspaceSolution(
        Solution Solution,
        ImmutableArray<string> ProjectFallbackPaths);
}
