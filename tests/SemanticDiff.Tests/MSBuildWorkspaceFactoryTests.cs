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
}
