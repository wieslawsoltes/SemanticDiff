using System.Collections.Immutable;
using System.Xml.Linq;
using System.Xml;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics.Xaml;

public sealed class XamlSemanticProvider : ISemanticProvider
{
    private readonly XmlParserRoslynXamlParser parser = new();

    public string Id => "xamlx-xmlparser-roslyn-adapter";

    public bool CanAnalyze(GitFileChange fileChange)
    {
        var extension = Path.GetExtension(fileChange.Path);
        return extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask<SemanticGraph> AnalyzeAsync(SemanticAnalysisRequest request, CancellationToken cancellationToken)
    {
        var anchors = ImmutableArray.CreateBuilder<SemanticAnchor>();
        var edges = ImmutableArray.CreateBuilder<SemanticEdge>();

        foreach (var document in request.Documents.Where(IsXamlLike))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceText = await LoadAnalysisTextAsync(request.RepositoryPath, document, cancellationToken).ConfigureAwait(false);
            AddXamlAnchors(document, parser.Parse(sourceText), anchors, edges);
        }

        return new SemanticGraph(anchors.ToImmutable(), edges.ToImmutable());
    }

    private static bool IsXamlLike(DiffDocumentSnapshot document) =>
        document.Metadata.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
        document.Metadata.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
        document.Metadata.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> LoadAnalysisTextAsync(string repositoryPath, DiffDocumentSnapshot document, CancellationToken cancellationToken)
    {
        if (document.Metadata.Status != DiffFileStatus.Deleted && !string.IsNullOrWhiteSpace(repositoryPath))
        {
            var filePath = Path.Combine(repositoryPath, document.Metadata.Path);
            if (File.Exists(filePath))
            {
                return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            }
        }

        return CreateSourceText(document);
    }

    private static string CreateSourceText(DiffDocumentSnapshot document) => string.Join(Environment.NewLine, document.Lines
        .Where(line => line.Kind != DiffLineKind.Metadata && line.Kind != DiffLineKind.Imaginary)
        .Select(line => CleanDiffLine(line.Text)));

    private static void AddXamlAnchors(
        DiffDocumentSnapshot document,
        XamlSyntaxDocument parsedDocument,
        ImmutableArray<SemanticAnchor>.Builder anchors,
        ImmutableArray<SemanticEdge>.Builder edges)
    {
        foreach (var diagnostic in parsedDocument.Diagnostics)
        {
            AddAnchor(anchors, new SemanticAnchor(
                $"{document.Id}:xaml-diagnostic:{diagnostic.Line}:{diagnostic.Column}",
                document.Id,
                new TextRange(0, 0, diagnostic.Line, diagnostic.Column),
                SemanticAnchorKind.Unknown,
                $"XML parse error: {diagnostic.Message}"));
        }

        var root = parsedDocument.Document?.Root;
        if (root is not null)
        {
            var namespaceMap = XamlNamespaceMap.Create(root);
            var rootAnchor = AddAnchor(anchors, new SemanticAnchor(
                $"{document.Id}:xaml-root:{ResolveTypeName(root, namespaceMap)}",
                document.Id,
                GetRange(root, root.Name.LocalName.Length),
                SemanticAnchorKind.XamlRoot,
                ResolveTypeName(root, namespaceMap)));

            AddNamespaceAnchors(document, root, rootAnchor, namespaceMap, anchors, edges);
            AddElementAnchors(document, root, rootAnchor, namespaceMap, anchors, edges);
            return;
        }

        var sourceText = parsedDocument.SourceText;
        var classAttribute = FindAttributeValue(sourceText, "x:Class");
        if (classAttribute is not null)
        {
            var rootAnchorId = $"{document.Id}:xaml-root:fallback";
            var anchorId = $"{document.Id}:xaml-class:{classAttribute.Value}";
            AddAnchor(anchors, new SemanticAnchor(rootAnchorId, document.Id, new TextRange(0, 0, 1, 1), SemanticAnchorKind.XamlRoot, document.Metadata.Path));
            AddAnchor(anchors, new SemanticAnchor(anchorId, document.Id, new TextRange(classAttribute.StartIndex, classAttribute.Value.Length, classAttribute.Line, classAttribute.Column), SemanticAnchorKind.Type, classAttribute.Value));
            AddEdge(edges, rootAnchorId, anchorId, SemanticEdgeKind.XamlClass, 0.72, "x:Class");
        }
    }

    private static void AddNamespaceAnchors(
        DiffDocumentSnapshot document,
        XElement root,
        SemanticAnchor rootAnchor,
        XamlNamespaceMap namespaceMap,
        ImmutableArray<SemanticAnchor>.Builder anchors,
        ImmutableArray<SemanticEdge>.Builder edges)
    {
        foreach (var item in namespaceMap.Namespaces)
        {
            var displayName = string.IsNullOrWhiteSpace(item.Prefix)
                ? item.NamespaceName
                : $"{item.Prefix}={item.NamespaceName}";
            var anchor = AddAnchor(anchors, new SemanticAnchor(
                $"{document.Id}:xaml-namespace:{displayName}",
                document.Id,
                GetRange(root, displayName.Length),
                SemanticAnchorKind.Namespace,
                displayName));
            AddEdge(edges, rootAnchor.Id, anchor.Id, SemanticEdgeKind.Contains, 0.7, "xmlns");
        }
    }

    private static void AddElementAnchors(
        DiffDocumentSnapshot document,
        XElement root,
        SemanticAnchor rootAnchor,
        XamlNamespaceMap namespaceMap,
        ImmutableArray<SemanticAnchor>.Builder anchors,
        ImmutableArray<SemanticEdge>.Builder edges)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            var elementTypeName = ResolveTypeName(element, namespaceMap);
            var elementAnchor = element == root
                ? rootAnchor
                : AddAnchor(anchors, new SemanticAnchor(
                    $"{document.Id}:xaml-element:{elementTypeName}:{GetLine(element)}:{GetColumn(element)}",
                    document.Id,
                    GetRange(element, element.Name.LocalName.Length),
                    SemanticAnchorKind.Type,
                    elementTypeName));

            if (element != root)
            {
                AddEdge(edges, rootAnchor.Id, elementAnchor.Id, SemanticEdgeKind.Contains, 0.68, element.Name.LocalName);
            }

            foreach (var attribute in element.Attributes())
            {
                AddAttributeAnchor(document, attribute, elementAnchor, rootAnchor, anchors, edges);
            }
        }
    }

    private static void AddAttributeAnchor(
        DiffDocumentSnapshot document,
        XAttribute attribute,
        SemanticAnchor elementAnchor,
        SemanticAnchor rootAnchor,
        ImmutableArray<SemanticAnchor>.Builder anchors,
        ImmutableArray<SemanticEdge>.Builder edges)
    {
        if (attribute.IsNamespaceDeclaration)
        {
            return;
        }

        var localName = attribute.Name.LocalName;
        if (localName == "Class")
        {
            var classAnchor = AddAnchor(anchors, new SemanticAnchor(
                $"{document.Id}:xaml-class:{attribute.Value}",
                document.Id,
                GetRange(attribute, attribute.Value.Length),
                SemanticAnchorKind.Type,
                attribute.Value));
            AddEdge(edges, rootAnchor.Id, classAnchor.Id, SemanticEdgeKind.XamlClass, 0.94, "x:Class");
            return;
        }

        if (localName is "Name" or "Key")
        {
            var kind = localName == "Key" ? SemanticAnchorKind.Resource : SemanticAnchorKind.XamlName;
            var edgeKind = localName == "Key" ? SemanticEdgeKind.Resource : SemanticEdgeKind.Contains;
            var anchor = AddAnchor(anchors, new SemanticAnchor(
                $"{document.Id}:xaml-{localName}:{attribute.Value}",
                document.Id,
                GetRange(attribute, attribute.Value.Length),
                kind,
                attribute.Value));
            AddEdge(edges, elementAnchor.Id, anchor.Id, edgeKind, 0.86, localName);
            return;
        }

        foreach (var markupReference in MarkupReference.Parse(attribute.Value))
        {
            var kind = markupReference.Kind == SemanticEdgeKind.Binding ? SemanticAnchorKind.Binding : SemanticAnchorKind.Resource;
            var anchor = AddAnchor(anchors, new SemanticAnchor(
                $"{document.Id}:xaml-{markupReference.Kind}:{attribute.Name.LocalName}:{markupReference.Value}",
                document.Id,
                GetRange(attribute, markupReference.Value.Length),
                kind,
                markupReference.Value));
            AddEdge(edges, elementAnchor.Id, anchor.Id, markupReference.Kind, markupReference.Confidence, attribute.Name.LocalName);
        }
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

    private static string ResolveTypeName(XElement element, XamlNamespaceMap namespaceMap)
    {
        var namespaceName = element.Name.NamespaceName;
        if (namespaceMap.TryGetClrNamespace(namespaceName, out var clrNamespace))
        {
            return $"{clrNamespace}.{element.Name.LocalName}";
        }

        if (namespaceMap.TryGetPrefix(namespaceName, out var prefix) && !string.IsNullOrWhiteSpace(prefix))
        {
            return $"{prefix}:{element.Name.LocalName}";
        }

        return element.Name.LocalName;
    }

    private static TextRange GetRange(XObject xobject, int length)
    {
        return new TextRange(0, Math.Max(0, length), GetLine(xobject), GetColumn(xobject));
    }

    private static int GetLine(XObject xobject) => xobject is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;

    private static int GetColumn(XObject xobject) => xobject is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LinePosition : 1;

    private static string CleanDiffLine(string text) => text.Length > 0 && text[0] is '+' or '-' ? text[1..] : text;

    private static AttributeValue? FindAttributeValue(string text, string attributeName)
    {
        var attributeIndex = text.IndexOf(attributeName, StringComparison.Ordinal);
        if (attributeIndex < 0)
        {
            return null;
        }

        var quoteStart = text.IndexOf('"', attributeIndex);
        if (quoteStart < 0)
        {
            return null;
        }

        var quoteEnd = text.IndexOf('"', quoteStart + 1);
        if (quoteEnd <= quoteStart)
        {
            return null;
        }

        var valueStart = quoteStart + 1;
        var (line, column) = GetLineColumn(text, valueStart);
        return new AttributeValue(text[valueStart..quoteEnd], valueStart, line, column);
    }

    private static (int Line, int Column) GetLineColumn(string text, int index)
    {
        var line = 1;
        var column = 1;
        for (var textIndex = 0; textIndex < Math.Min(index, text.Length); textIndex++)
        {
            if (text[textIndex] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }

    private sealed record AttributeValue(string Value, int StartIndex, int Line, int Column);

    private sealed class XamlNamespaceMap
    {
        private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";
        private readonly Dictionary<string, string> prefixByNamespace = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> clrNamespaceByNamespace = new(StringComparer.Ordinal);

        private XamlNamespaceMap()
        {
        }

        public IReadOnlyList<XamlNamespaceDeclaration> Namespaces { get; private set; } = [];

        public static XamlNamespaceMap Create(XElement root)
        {
            var declarations = ImmutableArray.CreateBuilder<XamlNamespaceDeclaration>();
            var map = new XamlNamespaceMap();

            foreach (var attribute in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                var prefix = attribute.Name.NamespaceName == XmlnsNamespace ? attribute.Name.LocalName : string.Empty;
                var namespaceName = attribute.Value;
                declarations.Add(new XamlNamespaceDeclaration(prefix, namespaceName));
                map.prefixByNamespace[namespaceName] = prefix;

                var clrNamespace = ParseClrNamespace(namespaceName);
                if (!string.IsNullOrWhiteSpace(clrNamespace))
                {
                    map.clrNamespaceByNamespace[namespaceName] = clrNamespace;
                }
            }

            map.Namespaces = declarations.ToImmutable();
            return map;
        }

        public bool TryGetPrefix(string namespaceName, out string prefix) => prefixByNamespace.TryGetValue(namespaceName, out prefix!);

        public bool TryGetClrNamespace(string namespaceName, out string clrNamespace) => clrNamespaceByNamespace.TryGetValue(namespaceName, out clrNamespace!);

        private static string? ParseClrNamespace(string namespaceName)
        {
            if (!namespaceName.StartsWith("clr-namespace:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var value = namespaceName["clr-namespace:".Length..];
            var separatorIndex = value.IndexOf(';', StringComparison.Ordinal);
            return separatorIndex >= 0 ? value[..separatorIndex] : value;
        }
    }

    private sealed record XamlNamespaceDeclaration(string Prefix, string NamespaceName);

    private sealed record MarkupReference(SemanticEdgeKind Kind, string Value, double Confidence)
    {
        public static IEnumerable<MarkupReference> Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value[0] != '{')
            {
                yield break;
            }

            var normalized = value.Trim('{', '}').Trim();
            if (normalized.StartsWith("Binding", StringComparison.OrdinalIgnoreCase))
            {
                var path = ReadNamedArgument(normalized, "Path") ?? ReadFirstArgument(normalized["Binding".Length..]) ?? "Binding";
                yield return new MarkupReference(SemanticEdgeKind.Binding, path, 0.82);
            }
            else if (normalized.StartsWith("StaticResource", StringComparison.OrdinalIgnoreCase))
            {
                yield return new MarkupReference(SemanticEdgeKind.Resource, ReadFirstArgument(normalized["StaticResource".Length..]) ?? "StaticResource", 0.82);
            }
            else if (normalized.StartsWith("DynamicResource", StringComparison.OrdinalIgnoreCase))
            {
                yield return new MarkupReference(SemanticEdgeKind.Resource, ReadFirstArgument(normalized["DynamicResource".Length..]) ?? "DynamicResource", 0.78);
            }
        }

        private static string? ReadFirstArgument(string text)
        {
            var normalized = text.Trim().TrimStart(',').Trim();
            var commaIndex = normalized.IndexOf(',', StringComparison.Ordinal);
            var argument = commaIndex >= 0 ? normalized[..commaIndex] : normalized;
            return string.IsNullOrWhiteSpace(argument) ? null : argument.Trim();
        }

        private static string? ReadNamedArgument(string text, string name)
        {
            var search = $"{name}=";
            var index = text.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var start = index + search.Length;
            var end = text.IndexOf(',', start);
            var value = end >= 0 ? text[start..end] : text[start..];
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}

public sealed record XamlParseDiagnostic(string Message, int Line, int Column);

public sealed record XamlSyntaxDocument(string SourceText, XDocument? Document, ImmutableArray<XamlParseDiagnostic> Diagnostics)
{
    public bool HasErrors => !Diagnostics.IsDefaultOrEmpty;
}

public sealed class XmlParserRoslynXamlParser
{
    public XamlSyntaxDocument Parse(string sourceText)
    {
        try
        {
            var document = XDocument.Parse(sourceText, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            return new XamlSyntaxDocument(sourceText, document, ImmutableArray<XamlParseDiagnostic>.Empty);
        }
        catch (XmlException exception)
        {
            return new XamlSyntaxDocument(
                sourceText,
                null,
                ImmutableArray.Create(new XamlParseDiagnostic(exception.Message, Math.Max(1, exception.LineNumber), Math.Max(1, exception.LinePosition))));
        }
    }
}