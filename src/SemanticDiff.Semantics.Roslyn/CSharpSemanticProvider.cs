using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics.Roslyn;

public sealed class CSharpSemanticProvider : ISemanticProvider
{
    private readonly MSBuildWorkspaceFactory workspaceFactory;

    public CSharpSemanticProvider()
        : this(new MSBuildWorkspaceFactory())
    {
    }

    public CSharpSemanticProvider(MSBuildWorkspaceFactory workspaceFactory)
    {
        this.workspaceFactory = workspaceFactory;
    }

    public string Id => "roslyn-csharp";

    public bool CanAnalyze(GitFileChange fileChange) => fileChange.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<SemanticGraph> AnalyzeAsync(SemanticAnalysisRequest request, CancellationToken cancellationToken)
    {
        var csharpDocuments = request.Documents
            .Where(document => document.Metadata.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();

        if (csharpDocuments.Length == 0)
        {
            return SemanticGraph.Empty;
        }

        SemanticGraph? workspaceGraph = null;
        if (request.AnalysisMode == SemanticAnalysisMode.WorkspaceThenSyntax && !string.IsNullOrWhiteSpace(request.RepositoryPath))
        {
            try
            {
                workspaceGraph = await TryAnalyzeWithWorkspaceAsync(request.RepositoryPath, csharpDocuments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                workspaceGraph = null;
            }
        }

        if (workspaceGraph is not null)
        {
            var coveredDocumentIds = GetCoveredDocumentIds(workspaceGraph);
            if (csharpDocuments.All(document => coveredDocumentIds.Contains(document.Id)))
            {
                return workspaceGraph;
            }

            var syntaxGraph = await AnalyzeWithInMemoryCompilationAsync(request.RepositoryPath, csharpDocuments, cancellationToken).ConfigureAwait(false);
            return MergeGraphs(workspaceGraph, syntaxGraph);
        }

        return await AnalyzeWithInMemoryCompilationAsync(request.RepositoryPath, csharpDocuments, cancellationToken).ConfigureAwait(false);
    }

    private static ImmutableHashSet<DiffDocumentId> GetCoveredDocumentIds(SemanticGraph graph) => graph.Anchors
        .Where(anchor => anchor.Kind == SemanticAnchorKind.File)
        .Select(anchor => anchor.DocumentId)
        .ToImmutableHashSet();

    private static SemanticGraph MergeGraphs(SemanticGraph first, SemanticGraph second)
    {
        var anchors = first.Anchors
            .Concat(second.Anchors)
            .GroupBy(anchor => anchor.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToImmutableArray();
        var edges = first.Edges
            .Concat(second.Edges)
            .GroupBy(edge => edge.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(edge => edge.Confidence).First())
            .ToImmutableArray();
        return new SemanticGraph(anchors, edges);
    }

    private async ValueTask<SemanticGraph?> TryAnalyzeWithWorkspaceAsync(
        string repositoryPath,
        ImmutableArray<DiffDocumentSnapshot> documents,
        CancellationToken cancellationToken)
    {
        var workspacePath = MSBuildWorkspaceFactory.FindWorkspacePath(repositoryPath);
        if (workspacePath is null)
        {
            return null;
        }

        try
        {
            using var workspace = workspaceFactory.CreateWorkspace();
            var solution = workspacePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await workspace.OpenProjectAsync(workspacePath, cancellationToken: cancellationToken).ConfigureAwait(false)).Solution
                : await workspace.OpenSolutionAsync(workspacePath, cancellationToken: cancellationToken).ConfigureAwait(false);

            var documentContexts = ImmutableArray.CreateBuilder<CSharpDocumentContext>();
            foreach (var snapshot in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fullPath = Path.GetFullPath(Path.Combine(repositoryPath, snapshot.Metadata.Path));
                var roslynDocument = solution.Projects
                    .SelectMany(project => project.Documents)
                    .FirstOrDefault(document => string.Equals(document.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

                if (roslynDocument is null)
                {
                    continue;
                }

                var syntaxTree = await roslynDocument.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = await roslynDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (syntaxTree is null || semanticModel is null)
                {
                    continue;
                }

                documentContexts.Add(new CSharpDocumentContext(snapshot, syntaxTree, semanticModel, await syntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false)));
            }

            return documentContexts.Count == 0
                ? null
                : await AnalyzeContextsAsync(documentContexts.ToImmutable(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async ValueTask<SemanticGraph> AnalyzeWithInMemoryCompilationAsync(
        string repositoryPath,
        ImmutableArray<DiffDocumentSnapshot> documents,
        CancellationToken cancellationToken)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var trees = ImmutableArray.CreateBuilder<SyntaxTree>(documents.Length);
        var snapshotsByTree = new Dictionary<SyntaxTree, DiffDocumentSnapshot>();

        foreach (var document in documents)
        {
            var sourceText = SourceText.From(await LoadAnalysisTextAsync(repositoryPath, document, cancellationToken).ConfigureAwait(false));
            var tree = CSharpSyntaxTree.ParseText(sourceText, parseOptions, document.Metadata.Path, cancellationToken);
            trees.Add(tree);
            snapshotsByTree.Add(tree, document);
        }

        var compilation = CSharpCompilation.Create(
            "SemanticDiffAnalysis",
            trees,
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var contexts = ImmutableArray.CreateBuilder<CSharpDocumentContext>(trees.Count);
        foreach (var tree in trees)
        {
            contexts.Add(new CSharpDocumentContext(
                snapshotsByTree[tree],
                tree,
                compilation.GetSemanticModel(tree, ignoreAccessibility: true),
                await tree.GetTextAsync(cancellationToken).ConfigureAwait(false)));
        }

        return await AnalyzeContextsAsync(contexts.ToImmutable(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> LoadAnalysisTextAsync(string repositoryPath, DiffDocumentSnapshot document, CancellationToken cancellationToken)
    {
        if (document.Metadata.Status != DiffFileStatus.Deleted)
        {
            var filePath = Path.Combine(repositoryPath, document.Metadata.Path);
            if (File.Exists(filePath))
            {
                return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            }
        }

        return string.Join(Environment.NewLine, document.Lines
            .Where(line => line.Kind != DiffLineKind.Metadata && line.Kind != DiffLineKind.Imaginary)
            .Select(line => line.Text));
    }

    private static async ValueTask<SemanticGraph> AnalyzeContextsAsync(
        ImmutableArray<CSharpDocumentContext> contexts,
        CancellationToken cancellationToken)
    {
        var anchors = ImmutableArray.CreateBuilder<SemanticAnchor>();
        var edges = ImmutableArray.CreateBuilder<SemanticEdge>();
        var typeAnchorsByFullName = new Dictionary<string, List<SemanticAnchor>>(StringComparer.Ordinal);
        var typeAnchorsBySimpleName = new Dictionary<string, List<SemanticAnchor>>(StringComparer.Ordinal);
        var memberAnchorsBySymbolKey = new Dictionary<string, SemanticAnchor>(StringComparer.Ordinal);
        var typeDeclarationAnchors = new Dictionary<BaseTypeDeclarationSyntax, SemanticAnchor>();
        var fileAnchors = new Dictionary<DiffDocumentId, SemanticAnchor>();

        foreach (var context in contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await context.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            var fileAnchor = AddAnchor(anchors, new SemanticAnchor(
                $"{context.Snapshot.Id}:file",
                context.Snapshot.Id,
                new TextRange(0, 0, 1, 1),
                SemanticAnchorKind.File,
                context.Snapshot.Metadata.Path));
            fileAnchors[context.Snapshot.Id] = fileAnchor;

            foreach (var namespaceDeclaration in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            {
                var range = GetTextRange(context.SourceText, namespaceDeclaration.Name.Span);
                var namespaceAnchor = AddAnchor(anchors, new SemanticAnchor(
                    $"{context.Snapshot.Id}:namespace:{namespaceDeclaration.Name}",
                    context.Snapshot.Id,
                    range,
                    SemanticAnchorKind.Namespace,
                    namespaceDeclaration.Name.ToString()));
                AddEdge(edges, fileAnchor.Id, namespaceAnchor.Id, SemanticEdgeKind.Contains, 0.95, "namespace");
            }

            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var symbol = context.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) as INamedTypeSymbol;
                var fullName = GetTypeKey(symbol, declaration);
                var simpleName = declaration.Identifier.ValueText;
                var anchor = AddAnchor(anchors, new SemanticAnchor(
                    $"{context.Snapshot.Id}:type:{fullName}",
                    context.Snapshot.Id,
                    GetTextRange(context.SourceText, declaration.Identifier.Span),
                    SemanticAnchorKind.Type,
                    fullName));

                AddToLookup(typeAnchorsByFullName, fullName, anchor);
                AddToLookup(typeAnchorsBySimpleName, simpleName, anchor);
                typeDeclarationAnchors[declaration] = anchor;
                AddEdge(edges, fileAnchor.Id, anchor.Id, SemanticEdgeKind.Contains, 0.96, declaration.Kind().ToString());
            }
        }

        foreach (var context in contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await context.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);

            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (!typeDeclarationAnchors.TryGetValue(declaration, out var sourceAnchor))
                {
                    continue;
                }

                AddMemberAnchors(context, declaration, sourceAnchor, anchors, edges, memberAnchorsBySymbolKey, cancellationToken);

                if (declaration.BaseList is not null)
                {
                    foreach (var baseType in declaration.BaseList.Types)
                    {
                        var targetAnchor = ResolveTypeAnchor(context.SemanticModel, baseType.Type, typeAnchorsByFullName, typeAnchorsBySimpleName, cancellationToken);
                        if (targetAnchor is not null && targetAnchor.Id != sourceAnchor.Id)
                        {
                            AddEdge(edges, sourceAnchor.Id, targetAnchor.Id, SemanticEdgeKind.TypeInheritance, 0.86, baseType.Type.ToString());
                        }
                    }
                }
            }

            AddReferenceEdges(context, typeDeclarationAnchors, typeAnchorsByFullName, typeAnchorsBySimpleName, memberAnchorsBySymbolKey, edges, cancellationToken);
        }

        AddPartialClassEdges(typeAnchorsByFullName, edges);
        return new SemanticGraph(anchors.ToImmutable(), edges.ToImmutable());
    }

    private static void AddMemberAnchors(
        CSharpDocumentContext context,
        BaseTypeDeclarationSyntax declaration,
        SemanticAnchor typeAnchor,
        ImmutableArray<SemanticAnchor>.Builder anchors,
        ImmutableArray<SemanticEdge>.Builder edges,
        Dictionary<string, SemanticAnchor> memberAnchorsBySymbolKey,
        CancellationToken cancellationToken)
    {
        if (declaration is not TypeDeclarationSyntax typeDeclaration)
        {
            return;
        }

        foreach (var member in typeDeclaration.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = context.SemanticModel.GetDeclaredSymbol(member, cancellationToken);
            var nameSpan = GetMemberNameSpan(member);
            if (symbol is null || nameSpan is null)
            {
                continue;
            }

            var memberKey = GetSymbolKey(symbol);
            var anchor = AddAnchor(anchors, new SemanticAnchor(
                $"{context.Snapshot.Id}:member:{memberKey}",
                context.Snapshot.Id,
                GetTextRange(context.SourceText, nameSpan.Value),
                SemanticAnchorKind.Member,
                symbol.Name));

            memberAnchorsBySymbolKey.TryAdd(memberKey, anchor);
            AddEdge(edges, typeAnchor.Id, anchor.Id, SemanticEdgeKind.Contains, 0.92, symbol.Kind.ToString());
        }
    }

    private static void AddReferenceEdges(
        CSharpDocumentContext context,
        Dictionary<BaseTypeDeclarationSyntax, SemanticAnchor> typeDeclarationAnchors,
        Dictionary<string, List<SemanticAnchor>> typeAnchorsByFullName,
        Dictionary<string, List<SemanticAnchor>> typeAnchorsBySimpleName,
        Dictionary<string, SemanticAnchor> memberAnchorsBySymbolKey,
        ImmutableArray<SemanticEdge>.Builder edges,
        CancellationToken cancellationToken)
    {
        var root = context.SyntaxTree.GetRoot(cancellationToken);
        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = context.SemanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol;
            if (symbol is null)
            {
                continue;
            }

            var sourceType = identifier.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            if (sourceType is null || !typeDeclarationAnchors.TryGetValue(sourceType, out var sourceAnchor))
            {
                continue;
            }

            var targetAnchor = symbol is INamedTypeSymbol namedType
                ? ResolveTypeAnchor(namedType, typeAnchorsByFullName, typeAnchorsBySimpleName)
                : memberAnchorsBySymbolKey.GetValueOrDefault(GetSymbolKey(symbol));

            if (targetAnchor is not null && targetAnchor.Id != sourceAnchor.Id)
            {
                AddEdge(edges, sourceAnchor.Id, targetAnchor.Id, SemanticEdgeKind.SymbolReference, 0.72, identifier.Identifier.ValueText);
            }
        }
    }

    private static void AddPartialClassEdges(Dictionary<string, List<SemanticAnchor>> typeAnchorsByFullName, ImmutableArray<SemanticEdge>.Builder edges)
    {
        foreach (var group in typeAnchorsByFullName.Values.Where(group => group.Select(anchor => anchor.DocumentId).Distinct().Count() > 1))
        {
            var ordered = group.OrderBy(anchor => anchor.DocumentId.Value, StringComparer.Ordinal).ToArray();
            for (var anchorIndex = 0; anchorIndex < ordered.Length - 1; anchorIndex++)
            {
                AddEdge(edges, ordered[anchorIndex].Id, ordered[anchorIndex + 1].Id, SemanticEdgeKind.PartialClass, 0.88, "partial");
            }
        }
    }

    private static SemanticAnchor? ResolveTypeAnchor(
        SemanticModel semanticModel,
        TypeSyntax typeSyntax,
        Dictionary<string, List<SemanticAnchor>> typeAnchorsByFullName,
        Dictionary<string, List<SemanticAnchor>> typeAnchorsBySimpleName,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetSymbolInfo(typeSyntax, cancellationToken).Symbol as INamedTypeSymbol;
        if (symbol is not null)
        {
            var anchor = ResolveTypeAnchor(symbol, typeAnchorsByFullName, typeAnchorsBySimpleName);
            if (anchor is not null)
            {
                return anchor;
            }
        }

        return GetSingle(typeAnchorsBySimpleName, typeSyntax.ToString());
    }

    private static SemanticAnchor? ResolveTypeAnchor(
        INamedTypeSymbol symbol,
        Dictionary<string, List<SemanticAnchor>> typeAnchorsByFullName,
        Dictionary<string, List<SemanticAnchor>> typeAnchorsBySimpleName)
    {
        return GetSingle(typeAnchorsByFullName, GetTypeDisplayName(symbol)) ?? GetSingle(typeAnchorsBySimpleName, symbol.Name);
    }

    private static SemanticAnchor? GetSingle(Dictionary<string, List<SemanticAnchor>> lookup, string key)
    {
        return lookup.TryGetValue(key, out var anchors) && anchors.Count == 1 ? anchors[0] : null;
    }

    private static SemanticAnchor AddAnchor(ImmutableArray<SemanticAnchor>.Builder anchors, SemanticAnchor anchor)
    {
        if (!anchors.Any(existing => existing.Id == anchor.Id))
        {
            anchors.Add(anchor);
        }

        return anchor;
    }

    private static void AddEdge(ImmutableArray<SemanticEdge>.Builder edges, string sourceAnchorId, string targetAnchorId, SemanticEdgeKind kind, double confidence, string? label)
    {
        if (sourceAnchorId == targetAnchorId)
        {
            return;
        }

        var edgeId = $"{kind}:{sourceAnchorId}->{targetAnchorId}:{label}";
        if (!edges.Any(edge => edge.Id == edgeId))
        {
            edges.Add(new SemanticEdge(edgeId, sourceAnchorId, targetAnchorId, kind, confidence, label));
        }
    }

    private static void AddToLookup(Dictionary<string, List<SemanticAnchor>> lookup, string key, SemanticAnchor anchor)
    {
        if (!lookup.TryGetValue(key, out var anchors))
        {
            anchors = [];
            lookup.Add(key, anchors);
        }

        anchors.Add(anchor);
    }

    private static TextRange GetTextRange(SourceText sourceText, TextSpan span)
    {
        var lineSpan = sourceText.Lines.GetLinePositionSpan(span);
        return new TextRange(span.Start, span.Length, lineSpan.Start.Line + 1, lineSpan.Start.Character + 1);
    }

    private static TextSpan? GetMemberNameSpan(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.Span,
        PropertyDeclarationSyntax property => property.Identifier.Span,
        EventDeclarationSyntax eventDeclaration => eventDeclaration.Identifier.Span,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Span,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.Span,
        _ => null
    };

    private static string GetTypeKey(INamedTypeSymbol? symbol, BaseTypeDeclarationSyntax declaration) =>
        symbol is null ? BuildQualifiedName(declaration) : GetTypeDisplayName(symbol);

    private static string GetTypeDisplayName(INamedTypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static string GetSymbolKey(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static string BuildQualifiedName(BaseTypeDeclarationSyntax declaration)
    {
        var names = new Stack<string>();
        names.Push(declaration.Identifier.ValueText);

        for (var parent = declaration.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case BaseTypeDeclarationSyntax parentType:
                    names.Push(parentType.Identifier.ValueText);
                    break;
                case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                    names.Push(namespaceDeclaration.Name.ToString());
                    break;
            }
        }

        return string.Join('.', names);
    }

    private static IEnumerable<MetadataReference> CreateMetadataReferences()
    {
        var assemblies = new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Task).Assembly
        };

        foreach (var assembly in assemblies.Distinct())
        {
            if (!string.IsNullOrWhiteSpace(assembly.Location))
            {
                yield return MetadataReference.CreateFromFile(assembly.Location);
            }
        }
    }

    private sealed record CSharpDocumentContext(
        DiffDocumentSnapshot Snapshot,
        SyntaxTree SyntaxTree,
        SemanticModel SemanticModel,
        SourceText SourceText);
}

public class MSBuildWorkspaceFactory
{
    public virtual MSBuildWorkspace CreateWorkspace() => MSBuildWorkspace.Create();

    public static string? FindWorkspacePath(string repositoryPath)
    {
        if (!Directory.Exists(repositoryPath))
        {
            return null;
        }

        return Directory.EnumerateFiles(repositoryPath, "*.slnx", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? Directory.EnumerateFiles(repositoryPath, "*.sln", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? Directory.EnumerateFiles(repositoryPath, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                               !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }
}