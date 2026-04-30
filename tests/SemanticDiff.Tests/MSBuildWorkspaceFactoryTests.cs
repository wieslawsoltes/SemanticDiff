using SemanticDiff.Core;
using SemanticDiff.Git;
using SemanticDiff.Semantics.Roslyn;

namespace SemanticDiff.Tests;

public sealed class MSBuildWorkspaceFactoryTests
{
    [Fact]
    public void FindWorkspacePath_PrefersRecursiveSourceSolutionOverBuildProject()
    {
        using var temp = new TemporaryRepository();
        temp.Write("build/Uno.UI.Build.csproj", "<Project />");
        temp.Write("tools/ResourcesExtractor/ResourcesExtractor.sln", string.Empty);
        temp.Write("src/Uno.UI.slnx", string.Empty);
        temp.Write("src/Controls/Uno.UI.Controls.csproj", "<Project />");

        var workspacePath = MSBuildWorkspaceFactory.FindWorkspacePath(temp.Root);

        Assert.Equal(temp.Path("src/Uno.UI.slnx"), workspacePath);
    }

    [Fact]
    public void FindWorkspacePath_FallsBackToSortedProjectsWhenNoSolutionExists()
    {
        using var temp = new TemporaryRepository();
        temp.Write("obj/Ignored.csproj", "<Project />");
        temp.Write("src/B/B.csproj", "<Project />");
        temp.Write("src/A/A.csproj", "<Project />");

        var workspacePath = MSBuildWorkspaceFactory.FindWorkspacePath(temp.Root);

        Assert.Equal(temp.Path("src/A/A.csproj"), workspacePath);
    }

    [Fact]
    public void FindProjectPaths_ReturnsAllNonIgnoredProjects()
    {
        using var temp = new TemporaryRepository();
        temp.Write("src/A/A.csproj", "<Project />");
        temp.Write("src/B/B.csproj", "<Project />");
        temp.Write("src/B/bin/Ignored.csproj", "<Project />");
        temp.Write(".git/Ignored.csproj", "<Project />");

        var projects = MSBuildWorkspaceFactory.FindProjectPaths(temp.Root);

        Assert.Equal(2, projects.Length);
        Assert.Contains(temp.Path("src/A/A.csproj"), projects);
        Assert.Contains(temp.Path("src/B/B.csproj"), projects);
        Assert.DoesNotContain(temp.Path("src/B/bin/Ignored.csproj"), projects);
        Assert.DoesNotContain(temp.Path(".git/Ignored.csproj"), projects);
    }

    [Fact]
    public async Task LoadFilesAsync_UsesGitFileIndexWithoutOpeningWorkspace()
    {
        using var temp = new TemporaryRepository();
        temp.Write("SemanticDiff.slnx", string.Empty);
        temp.Write("src/App/App.cs", "public sealed class App { }");
        temp.Write("obj/Ignored.cs", "public sealed class Ignored { }");
        var runner = new FakeGitCommandRunner(arguments =>
            arguments.SequenceEqual(["ls-files", "-z", "--cached", "--others", "--exclude-standard"])
                ? new GitCommandResult(0, "SemanticDiff.slnx\0src/App/App.cs\0obj/Ignored.cs\0README.md\0", string.Empty)
                : new GitCommandResult(1, string.Empty, "unexpected"));
        var service = new MSBuildWorkspaceFileDiscoveryService(new ThrowingWorkspaceFactory(), runner);

        var result = await service.LoadFilesAsync(temp.Root, CancellationToken.None);

        Assert.Equal(temp.Path("SemanticDiff.slnx"), result.WorkspacePath);
        Assert.Contains(result.Files, file => file.Path == "src/App/App.cs" && file.Language == "C#");
        Assert.Contains(result.Files, file => file.Path == "README.md" && file.Language == "Markdown");
        Assert.DoesNotContain(result.Files, file => file.Path.Contains("obj", StringComparison.OrdinalIgnoreCase));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task LoadFilesAsync_FallsBackToFileSystemWhenGitIndexUnavailable()
    {
        using var temp = new TemporaryRepository();
        temp.Write("SemanticDiff.slnx", string.Empty);
        temp.Write("src/App/App.cs", "public sealed class App { }");
        temp.Write(".git/Ignored.cs", "public sealed class Ignored { }");
        temp.Write("node_modules/Ignored.js", "export const ignored = true;");
        var runner = new FakeGitCommandRunner(_ => new GitCommandResult(128, string.Empty, "not a git repository"));
        var service = new MSBuildWorkspaceFileDiscoveryService(new ThrowingWorkspaceFactory(), runner);

        var result = await service.LoadFilesAsync(temp.Root, CancellationToken.None);

        Assert.Equal(temp.Path("SemanticDiff.slnx"), result.WorkspacePath);
        Assert.Contains(result.Files, file => file.Path == "SemanticDiff.slnx" && file.Language == "Solution");
        Assert.Contains(result.Files, file => file.Path == "src/App/App.cs" && file.Language == "C#");
        Assert.DoesNotContain(result.Files, file => file.Path.Contains(".git", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Files, file => file.Path.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
        Assert.Single(runner.Calls);
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"semanticdiff-workspace-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Path(string relativePath) => System.IO.Path.Combine(
            Root,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relativePath, string content)
        {
            var path = Path(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ThrowingWorkspaceFactory : MSBuildWorkspaceFactory
    {
        public override Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace CreateWorkspace() =>
            throw new InvalidOperationException("Workspace file discovery should not open MSBuildWorkspace.");
    }
}
