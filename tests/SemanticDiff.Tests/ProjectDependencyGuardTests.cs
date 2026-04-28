using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace SemanticDiff.Tests;

public sealed class ProjectDependencyGuardTests
{
    [Fact]
    public void Workbench_RemainsUiFrameworkFree()
    {
        AssertProjectAndSourceDoNotContain(
            "src/SemanticDiff.Workbench/SemanticDiff.Workbench.csproj",
            projectForbiddenTokens: ["Microsoft.UI", "Windows", "SkiaSharp.Views", "Uno"],
            sourceForbiddenTokens: ["Microsoft.UI", "Windows.", "SkiaSharp.Views", "Uno"]);
    }

    [Fact]
    public void Rendering_RemainsUiFrameworkFree()
    {
        AssertProjectAndSourceDoNotContain(
            "src/SemanticDiff.Rendering/SemanticDiff.Rendering.csproj",
            projectForbiddenTokens: ["Microsoft.UI", "Windows", "Uno"],
            sourceForbiddenTokens: ["Microsoft.UI", "Windows.", "Uno"]);
    }

    [Fact]
    public void ControlsUno_DoesNotReferenceApp()
    {
        AssertProjectAndSourceDoNotContain(
            "src/SemanticDiff.Controls.Uno/SemanticDiff.Controls.Uno.csproj",
            projectForbiddenTokens: ["SemanticDiff.App"],
            sourceForbiddenTokens: ["SemanticDiff.App"]);
    }

    [Fact]
    public void ControlsSampleHost_ReferencesControlsWithoutReferencingApp()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "samples/SemanticDiff.Controls.Uno.Sample/SemanticDiff.Controls.Uno.Sample.csproj");
        var document = XDocument.Load(projectPath);
        var projectReferences = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (element.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/'))
            .ToArray();

        Assert.Contains(projectReferences, reference => reference.EndsWith("src/SemanticDiff.Controls.Uno/SemanticDiff.Controls.Uno.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(projectReferences, reference => reference.Contains("SemanticDiff.App", StringComparison.Ordinal));
        Assert.Single(projectReferences);

        AssertProjectAndSourceDoNotContain(
            "samples/SemanticDiff.Controls.Uno.Sample/SemanticDiff.Controls.Uno.Sample.csproj",
            projectForbiddenTokens: ["SemanticDiff.App"],
            sourceForbiddenTokens: ["SemanticDiff.App"]);
    }

    [Fact]
    public void ControlsSampleHost_UsesReusableThemeResourceKeys()
    {
        var repositoryRoot = FindRepositoryRoot();
        var samplePagePath = Path.Combine(repositoryRoot, "samples/SemanticDiff.Controls.Uno.Sample/MainPage.xaml");
        var controlsResourcesPath = Path.Combine(repositoryRoot, "src/SemanticDiff.Controls.Uno/Themes/SemanticDiffControls.xaml");
        var usedThemeKeys = Regex.Matches(File.ReadAllText(samplePagePath), "\\{ThemeResource\\s+([^}]+)\\}")
            .Select(match => match.Groups[1].Value.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var definedKeys = ReadXamlKeys(controlsResourcesPath);

        Assert.Empty(usedThemeKeys.Where(key => !definedKeys.Contains(key)).Order(StringComparer.Ordinal));
    }

    private static void AssertProjectAndSourceDoNotContain(
        string projectRelativePath,
        IReadOnlyCollection<string> projectForbiddenTokens,
        IReadOnlyCollection<string> sourceForbiddenTokens)
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, projectRelativePath);
        var projectDirectory = Path.GetDirectoryName(projectPath) ?? throw new InvalidOperationException($"Project path has no directory: {projectPath}");
        var offenders = new List<string>();

        InspectProjectFile(projectPath, repositoryRoot, projectForbiddenTokens, offenders);
        InspectSourceFiles(projectDirectory, repositoryRoot, sourceForbiddenTokens, offenders);

        Assert.Empty(offenders.Order(StringComparer.Ordinal));
    }

    private static void InspectProjectFile(string projectPath, string repositoryRoot, IReadOnlyCollection<string> forbiddenTokens, List<string> offenders)
    {
        var document = XDocument.Load(projectPath);
        var sdk = document.Root?.Attribute("Sdk")?.Value ?? string.Empty;
        AddOffenderIfForbidden(projectPath, repositoryRoot, "Sdk", sdk, forbiddenTokens, offenders);

        foreach (var element in document.Descendants().Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference" or "FrameworkReference"))
        {
            var include = element.Attribute("Include")?.Value ?? string.Empty;
            AddOffenderIfForbidden(projectPath, repositoryRoot, element.Name.LocalName, include, forbiddenTokens, offenders);
        }
    }

    private static void InspectSourceFiles(string projectDirectory, string repositoryRoot, IReadOnlyCollection<string> forbiddenTokens, List<string> offenders)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(IsInspectableSourceFile))
        {
            var text = File.ReadAllText(sourceFile);
            foreach (var token in forbiddenTokens.Where(token => text.Contains(token, StringComparison.Ordinal)))
            {
                offenders.Add($"{Path.GetRelativePath(repositoryRoot, sourceFile).Replace('\\', '/')}: source contains '{token}'");
            }
        }
    }

    private static bool IsInspectableSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension is ".cs" or ".xaml";
    }

    private static void AddOffenderIfForbidden(
        string path,
        string repositoryRoot,
        string itemKind,
        string value,
        IReadOnlyCollection<string> forbiddenTokens,
        List<string> offenders)
    {
        foreach (var token in forbiddenTokens.Where(token => value.Contains(token, StringComparison.Ordinal)))
        {
            offenders.Add($"{Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')}: {itemKind} '{value}' contains '{token}'");
        }
    }

    private static HashSet<string> ReadXamlKeys(string path)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path)
            .Descendants()
            .Select(element => element.Attribute(xaml + "Key")?.Value)
            .OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SemanticDiff.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SemanticDiff.slnx from the test output directory.");
    }
}
