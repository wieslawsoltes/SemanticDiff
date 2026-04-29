using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Semantics.Roslyn;

public sealed class RoslynCSharpCodeCompletionProvider : ICodeCompletionProvider, IDisposable
{
    private static readonly Lazy<MefHostServices> HostServices = new(CreateHostServices);
    private readonly SemaphoreSlim workspaceGate = new(1, 1);
    private MSBuildWorkspace? cachedWorkspace;
    private Solution? cachedSolution;
    private string? cachedRepositoryPath;
    private string? cachedDetectedWorkspacePath;
    private string? cachedWorkspacePath;
    private bool disposed;

    public async ValueTask<CodeCompletionResult> GetCompletionsAsync(
        CodeCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (disposed || !CanComplete(request.Document.Metadata))
        {
            return CodeCompletionResult.Empty(Math.Max(0, request.Column));
        }

        var sourceText = BuildSourceText(request.Document);
        var position = GetAbsolutePosition(request.Document, sourceText, request.LineIndex, request.Column);
        using var documentContext = await ResolveDocumentAsync(request, sourceText, cancellationToken).ConfigureAwait(false);
        if (documentContext is null)
        {
            return CodeCompletionResult.Empty(Math.Max(0, request.Column));
        }

        var document = documentContext.Document;
        var service = CompletionService.GetService(document);
        if (service is null)
        {
            return CodeCompletionResult.Empty(Math.Max(0, request.Column));
        }

        var completionList = await service.GetCompletionsAsync(
                document,
                position,
                CompletionTrigger.Invoke,
                roles: null,
                options: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (completionList is null || completionList.ItemsList.Count == 0)
        {
            return CodeCompletionResult.Empty(Math.Max(0, request.Column));
        }

        return CreateResult(sourceText, request, completionList);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cachedWorkspace?.Dispose();
        workspaceGate.Dispose();
    }

    private static bool CanComplete(DiffDocumentMetadata metadata)
    {
        var language = (metadata.Language ?? string.Empty).Trim();
        return language.Equals("C#", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("csharp", StringComparison.OrdinalIgnoreCase) ||
            metadata.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CompletionDocumentContext?> ResolveDocumentAsync(
        CodeCompletionRequest request,
        SourceText sourceText,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RepositoryPath))
        {
            var workspaceDocument = await TryResolveWorkspaceDocumentAsync(request, sourceText, cancellationToken).ConfigureAwait(false);
            if (workspaceDocument is not null)
            {
                return workspaceDocument;
            }
        }

        return CreateAdhocDocument(request, sourceText);
    }

    private async Task<CompletionDocumentContext?> TryResolveWorkspaceDocumentAsync(
        CodeCompletionRequest request,
        SourceText sourceText,
        CancellationToken cancellationToken)
    {
        var repositoryPath = request.RepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return null;
        }

        await workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return null;
            }

            var workspacePath = GetCachedWorkspacePath(repositoryPath);
            if (workspacePath is null)
            {
                return null;
            }

            if (!string.Equals(cachedWorkspacePath, workspacePath, StringComparison.OrdinalIgnoreCase) ||
                cachedWorkspace is null ||
                cachedSolution is null)
            {
                cachedWorkspace?.Dispose();
                cachedWorkspace = MSBuildWorkspace.Create(HostServices.Value);
                cachedWorkspacePath = workspacePath;
                cachedSolution = workspacePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? (await cachedWorkspace.OpenProjectAsync(workspacePath, cancellationToken: cancellationToken).ConfigureAwait(false)).Solution
                    : await cachedWorkspace.OpenSolutionAsync(workspacePath, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var fullPath = Path.GetFullPath(Path.Combine(repositoryPath, request.Document.Metadata.Path));
            var document = cachedSolution.Projects
                .SelectMany(project => project.Documents)
                .FirstOrDefault(document => string.Equals(document.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

            return document is null ? null : new CompletionDocumentContext(document.WithText(sourceText), OwnedWorkspace: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            cachedWorkspace?.Dispose();
            cachedWorkspace = null;
            cachedSolution = null;
            cachedWorkspacePath = null;
            return null;
        }
        finally
        {
            workspaceGate.Release();
        }
    }

    private string? GetCachedWorkspacePath(string repositoryPath)
    {
        var normalizedRepositoryPath = Path.GetFullPath(repositoryPath);
        if (!string.Equals(cachedRepositoryPath, normalizedRepositoryPath, StringComparison.OrdinalIgnoreCase))
        {
            cachedRepositoryPath = normalizedRepositoryPath;
            cachedDetectedWorkspacePath = MSBuildWorkspaceFactory.FindWorkspacePath(normalizedRepositoryPath);
        }

        return cachedDetectedWorkspacePath;
    }

    private static CompletionDocumentContext CreateAdhocDocument(CodeCompletionRequest request, SourceText sourceText)
    {
        var workspace = new AdhocWorkspace(HostServices.Value);
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "SemanticDiffCompletion",
            "SemanticDiffCompletion",
            LanguageNames.CSharp,
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: CreateMetadataReferences());
        var project = workspace.AddProject(projectInfo);
        var documentInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id),
            Path.GetFileName(request.Document.Metadata.Path),
            loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())),
            filePath: request.Document.Metadata.Path);
        var document = workspace.AddDocument(documentInfo);
        return new CompletionDocumentContext(document, workspace);
    }

    private static CodeCompletionResult CreateResult(
        SourceText sourceText,
        CodeCompletionRequest request,
        CompletionList completionList)
    {
        var span = completionList.Span;
        var lineSpan = sourceText.Lines.GetLinePositionSpan(span);
        var replacementStartColumn = request.Column;
        var replacementLength = 0;
        if (lineSpan.Start.Line == request.LineIndex && lineSpan.End.Line == request.LineIndex)
        {
            replacementStartColumn = lineSpan.Start.Character;
            replacementLength = Math.Max(0, lineSpan.End.Character - lineSpan.Start.Character);
        }

        var filterText = GetFilterText(sourceText, request.LineIndex, replacementStartColumn, request.Column);
        var maxItems = Math.Clamp(request.MaxItems, 1, 500);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = completionList.ItemsList
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayText))
            .Where(item => seen.Add(item.DisplayText))
            .Take(maxItems)
            .Select(MapItem)
            .ToImmutableArray();

        return new CodeCompletionResult(
            items,
            replacementStartColumn,
            replacementLength,
            filterText,
            completionList.ItemsList.Count > items.Length);
    }

    private static CodeCompletionItem MapItem(CompletionItem item)
    {
        var description = string.IsNullOrWhiteSpace(item.InlineDescription)
            ? string.Join(", ", item.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
            : item.InlineDescription;
        return new CodeCompletionItem(
            item.DisplayText,
            item.DisplayText,
            MapKind(item.Tags),
            string.IsNullOrWhiteSpace(description) ? "Roslyn C# completion" : description,
            item.FilterText,
            item.SortText,
            Priority: 100);
    }

    private static CodeCompletionItemKind MapKind(ImmutableArray<string> tags)
    {
        if (tags.Any(tag => tag.Equals("Keyword", StringComparison.OrdinalIgnoreCase)))
        {
            return CodeCompletionItemKind.Keyword;
        }

        if (tags.Any(tag => tag.Equals("Class", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Interface", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Enum", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Delegate", StringComparison.OrdinalIgnoreCase)))
        {
            return CodeCompletionItemKind.Type;
        }

        if (tags.Any(tag => tag.Equals("Method", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Function", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("ExtensionMethod", StringComparison.OrdinalIgnoreCase)))
        {
            return CodeCompletionItemKind.Function;
        }

        if (tags.Any(tag => tag.Equals("Property", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Field", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Event", StringComparison.OrdinalIgnoreCase)))
        {
            return CodeCompletionItemKind.Property;
        }

        if (tags.Any(tag => tag.Equals("Local", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Parameter", StringComparison.OrdinalIgnoreCase)))
        {
            return CodeCompletionItemKind.Variable;
        }

        if (tags.Any(tag => tag.Equals("Snippet", StringComparison.OrdinalIgnoreCase)))
        {
            return CodeCompletionItemKind.Snippet;
        }

        return CodeCompletionItemKind.Symbol;
    }

    private static SourceText BuildSourceText(DiffDocumentSnapshot document) =>
        SourceText.From(string.Join("\n", document.Lines
            .Where(line => line.Kind != DiffLineKind.Imaginary)
            .Select(line => line.Text ?? string.Empty)));

    private static int GetAbsolutePosition(DiffDocumentSnapshot document, SourceText sourceText, int lineIndex, int column)
    {
        if (document.Lines.IsDefaultOrEmpty)
        {
            return 0;
        }

        var clampedLine = Math.Clamp(lineIndex, 0, document.Lines.Length - 1);
        var sourceLine = sourceText.Lines[Math.Clamp(clampedLine, 0, sourceText.Lines.Count - 1)];
        return sourceLine.Start + Math.Clamp(column, 0, sourceLine.Span.Length);
    }

    private static string GetFilterText(SourceText sourceText, int lineIndex, int replacementStartColumn, int column)
    {
        if (lineIndex < 0 || lineIndex >= sourceText.Lines.Count)
        {
            return string.Empty;
        }

        var sourceLine = sourceText.Lines[lineIndex];
        var lineText = sourceLine.ToString();
        var start = Math.Clamp(replacementStartColumn, 0, lineText.Length);
        var end = Math.Clamp(column, start, lineText.Length);
        return lineText[start..end];
    }

    private static IEnumerable<MetadataReference> CreateMetadataReferences()
    {
        var assemblies = new[]
        {
            typeof(object).Assembly,
            typeof(Console).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Task).Assembly,
            typeof(System.Runtime.GCSettings).Assembly,
            typeof(System.ComponentModel.INotifyPropertyChanged).Assembly
        };

        return assemblies
            .Where(assembly => !string.IsNullOrWhiteSpace(assembly.Location))
            .DistinctBy(assembly => assembly.Location)
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location));
    }

    private static MefHostServices CreateHostServices()
    {
        var assemblies = MefHostServices.DefaultAssemblies.ToList();
        AddAssembly(assemblies, "Microsoft.CodeAnalysis.Features");
        AddAssembly(assemblies, "Microsoft.CodeAnalysis.CSharp.Features");
        return MefHostServices.Create(assemblies.Distinct());
    }

    private static void AddAssembly(List<Assembly> assemblies, string assemblyName)
    {
        try
        {
            assemblies.Add(Assembly.Load(assemblyName));
        }
        catch
        {
            // Features packages are optional at runtime; the document provider remains available.
        }
    }

    private sealed record CompletionDocumentContext(Document Document, IDisposable? OwnedWorkspace) : IDisposable
    {
        public void Dispose() => OwnedWorkspace?.Dispose();
    }
}
