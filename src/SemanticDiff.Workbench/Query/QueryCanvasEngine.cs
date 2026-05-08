using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Rendering;
using SemanticDiff.Semantics;
using SemanticDiff.Workbench.Symbols;

namespace SemanticDiff.Workbench.Query;

public sealed class QueryCanvasEngine
{
    private const int DefaultTakeLimit = 180;
    private const int HardTakeLimit = 500;

    public QueryCanvasExecutionResult Execute(string? query, QueryCanvasContext context, QueryCanvasScope scope)
    {
        try
        {
            var normalizedQuery = NormalizeQuery(query, scope);
            var parsed = QueryPipeline.Parse(normalizedQuery);
            var rows = ResolveRoot(parsed.Root, context, scope);
            var viewMode = parsed.Root.IsSymbolRoot ? context.DefaultSymbolViewMode : SymbolGraphViewMode.FilesAndSymbols;
            var hasExplicitTake = false;

            foreach (var operation in parsed.Operations)
            {
                rows = ApplyOperation(rows, operation, ref viewMode, ref hasExplicitTake);
            }

            if (!hasExplicitTake && rows.Count > DefaultTakeLimit)
            {
                rows = rows.Take(DefaultTakeLimit).ToList();
            }

            if (rows.Count > HardTakeLimit)
            {
                rows = rows.Take(HardTakeLimit).ToList();
            }

            return CreateResult(rows, parsed.Root, context, viewMode, normalizedQuery, hasExplicitTake);
        }
        catch (Exception exception) when (exception is QueryCanvasException or FormatException or ArgumentException or InvalidOperationException)
        {
            return QueryCanvasExecutionResult.Error(exception.Message);
        }
    }

    private static string NormalizeQuery(string? query, QueryCanvasScope scope)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            return query.Trim();
        }

        return scope == QueryCanvasScope.Workspace
            ? QueryCanvasSampleCatalog.WorkspaceOverview.Query
            : "Files.Where(f => f.IsChanged).Take(80)";
    }

    private static List<QueryEntity> ResolveRoot(QueryRoot root, QueryCanvasContext context, QueryCanvasScope scope)
    {
        var scopedDocuments = context.DocumentsForScope(scope);
        var diffDocuments = context.DiffDocuments.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : context.DiffDocuments;
        var workspaceDocuments = context.WorkspaceDocuments.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : context.WorkspaceDocuments;
        var symbols = context.Symbols.IsDefault ? ImmutableArray<SemanticNavigationItem>.Empty : context.Symbols;

        return root.Name.ToLowerInvariant() switch
        {
            "files" => scopedDocuments.Select(document => QueryEntity.FromDocument(document, context.MetricsFor(document))).ToList(),
            "changedfiles" => scopedDocuments.Where(IsChangedDocument).Select(document => QueryEntity.FromDocument(document, context.MetricsFor(document))).ToList(),
            "difffiles" => diffDocuments.Select(document => QueryEntity.FromDocument(document, context.MetricsFor(document))).ToList(),
            "workspacefiles" => workspaceDocuments.Select(document => QueryEntity.FromDocument(document, context.MetricsFor(document))).ToList(),
            "nodes" => scopedDocuments.Select(document => QueryEntity.FromDocument(document, context.MetricsFor(document))).ToList(),
            "symbols" => symbols.Select(QueryEntity.FromSymbol).ToList(),
            "changedsymbols" => symbols.Where(symbol => symbol.IsChanged).Select(QueryEntity.FromSymbol).ToList(),
            "linkedsymbols" => symbols.Where(symbol => symbol.IsLinked).Select(QueryEntity.FromSymbol).ToList(),
            _ => throw new QueryCanvasException($"Unknown query root '{root.Name}'. Use Files, ChangedFiles, DiffFiles, WorkspaceFiles, Nodes, Symbols, ChangedSymbols, or LinkedSymbols.")
        };
    }

    private static List<QueryEntity> ApplyOperation(
        List<QueryEntity> rows,
        QueryOperation operation,
        ref SymbolGraphViewMode viewMode,
        ref bool hasExplicitTake)
    {
        return operation.Name.ToLowerInvariant() switch
        {
            "where" => rows.Where(row => QueryPredicateEvaluator.Evaluate(row, operation.Argument)).ToList(),
            "take" => Take(rows, operation.Argument, ref hasExplicitTake),
            "skip" => rows.Skip(ParseCount(operation.Argument, "Skip")).ToList(),
            "orderby" => Order(rows, operation.Argument, descending: false),
            "orderbydescending" => Order(rows, operation.Argument, descending: true),
            "thenby" => Order(rows, operation.Argument, descending: false),
            "thenbydescending" => Order(rows, operation.Argument, descending: true),
            "map" => SetView(rows, SymbolGraphViewMode.FilesAndSymbols, ref viewMode),
            "graph" => SetView(rows, SymbolGraphViewMode.FilesAndSymbols, ref viewMode),
            "symbolsonly" => SetView(rows, SymbolGraphViewMode.SymbolsOnly, ref viewMode),
            "select" => rows,
            "count" => rows,
            "tolist" or "toarray" or "dump" => rows,
            _ => throw new QueryCanvasException($"Unsupported query operation '{operation.Name}'. Supported operations: Where, Take, Skip, OrderBy, OrderByDescending, Map, Graph, SymbolsOnly, ToList, ToArray, Dump.")
        };
    }

    private static List<QueryEntity> Take(List<QueryEntity> rows, string argument, ref bool hasExplicitTake)
    {
        hasExplicitTake = true;
        return rows.Take(Math.Min(HardTakeLimit, ParseCount(argument, "Take"))).ToList();
    }

    private static List<QueryEntity> SetView(List<QueryEntity> rows, SymbolGraphViewMode nextMode, ref SymbolGraphViewMode viewMode)
    {
        viewMode = nextMode;
        return rows;
    }

    private static int ParseCount(string argument, string operationName)
    {
        var trimmed = argument.Trim();
        if (!int.TryParse(trimmed, out var count) || count < 0)
        {
            throw new QueryCanvasException($"{operationName} expects a non-negative integer.");
        }

        return count;
    }

    private static List<QueryEntity> Order(List<QueryEntity> rows, string argument, bool descending)
    {
        var propertyName = QueryPredicateEvaluator.ExtractLambdaBody(argument);
        return descending
            ? rows.OrderByDescending(row => QueryPredicateEvaluator.GetPropertyValue(row, propertyName), QueryValueComparer.Instance).ToList()
            : rows.OrderBy(row => QueryPredicateEvaluator.GetPropertyValue(row, propertyName), QueryValueComparer.Instance).ToList();
    }

    private static QueryCanvasExecutionResult CreateResult(
        List<QueryEntity> rows,
        QueryRoot root,
        QueryCanvasContext context,
        SymbolGraphViewMode viewMode,
        string query,
        bool hasExplicitTake)
    {
        if (rows.Count == 0)
        {
            return new QueryCanvasExecutionResult(
                DiffCanvasScene.FromDocuments([]),
                QueryCanvasResultKind.Empty,
                "Query returned no results",
                query);
        }

        var documents = rows
            .Select(row => row.Document)
            .OfType<DiffDocumentSnapshot>()
            .DistinctBy(document => document.Id)
            .ToImmutableArray();
        var symbols = rows
            .Select(row => row.Symbol)
            .OfType<SemanticNavigationItem>()
            .DistinctBy(symbol => symbol.AnchorId)
            .ToImmutableArray();

        if (!symbols.IsDefaultOrEmpty)
        {
            var diffSourceDocuments = context.DiffDocuments.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : context.DiffDocuments;
            var workspaceSourceDocuments = context.WorkspaceDocuments.IsDefault ? ImmutableArray<DiffDocumentSnapshot>.Empty : context.WorkspaceDocuments;
            var sourceDocuments = diffSourceDocuments
                .Concat(workspaceSourceDocuments)
                .DistinctBy(document => document.Id)
                .ToImmutableArray();
            if (!documents.IsDefaultOrEmpty)
            {
                var documentIds = documents.Select(document => document.Id).ToHashSet();
                var extraSymbols = context.Symbols
                    .Where(symbol => documentIds.Contains(symbol.DocumentId))
                    .Where(symbol => !symbols.Any(existing => string.Equals(existing.AnchorId, symbol.AnchorId, StringComparison.Ordinal)))
                    .ToImmutableArray();
                if (!extraSymbols.IsDefaultOrEmpty)
                {
                    symbols = symbols.AddRange(extraSymbols);
                }
            }

            var buildResult = new SymbolGraphSceneBuilder().Build(new SymbolGraphSceneBuildRequest(
                symbols,
                context.SemanticGraph,
                sourceDocuments,
                context.LayoutMode,
                context.GroupingMode,
                viewMode));
            var kind = documents.IsDefaultOrEmpty && viewMode == SymbolGraphViewMode.SymbolsOnly
                ? QueryCanvasResultKind.Symbols
                : QueryCanvasResultKind.Mixed;
            return new QueryCanvasExecutionResult(
                buildResult.Scene,
                kind,
                FormatStatus(rows.Count, symbols.Length, buildResult.FileCount, hasExplicitTake),
                query);
        }

        var scene = DiffCanvasScene.FromDocuments(
            documents,
            context.SemanticGraph,
            null,
            context.EdgeOptions,
            [],
            context.AnnotationVisibility,
            context.GroupingMode);
        return new QueryCanvasExecutionResult(
            scene,
            QueryCanvasResultKind.Files,
            $"{documents.Length:N0} file node{Plural(documents.Length)} rendered",
            query);
    }

    private static string FormatStatus(int rowCount, int symbolCount, int fileCount, bool hasExplicitTake)
    {
        var capped = hasExplicitTake ? string.Empty : " | capped for responsiveness";
        if (fileCount > 0)
        {
            return $"{rowCount:N0} result{Plural(rowCount)} | {symbolCount:N0} symbols | {fileCount:N0} files{capped}";
        }

        return $"{symbolCount:N0} symbol node{Plural(symbolCount)} rendered{capped}";
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private static bool IsChangedDocument(DiffDocumentSnapshot document) =>
        document.Metadata.Status != DiffFileStatus.Unchanged ||
        document.Metadata.AddedLines > 0 ||
        document.Metadata.DeletedLines > 0 ||
        document.Lines.Any(line => line.Kind is DiffLineKind.Added or DiffLineKind.Deleted or DiffLineKind.Modified or DiffLineKind.Moved or DiffLineKind.Conflict);
}

internal sealed class QueryCanvasException(string message) : Exception(message);

internal sealed record QueryRoot(string Name)
{
    public bool IsSymbolRoot => Name.Equals("Symbols", StringComparison.OrdinalIgnoreCase) ||
                                Name.Equals("ChangedSymbols", StringComparison.OrdinalIgnoreCase) ||
                                Name.Equals("LinkedSymbols", StringComparison.OrdinalIgnoreCase);
}

internal sealed record QueryOperation(string Name, string Argument);

internal sealed record QueryPipeline(QueryRoot Root, ImmutableArray<QueryOperation> Operations)
{
    public static QueryPipeline Parse(string query)
    {
        var root = ReadRoot(query);
        var operations = ImmutableArray.CreateBuilder<QueryOperation>();
        var index = root.Name.Length;
        while (index < query.Length)
        {
            while (index < query.Length && char.IsWhiteSpace(query[index]))
            {
                index++;
            }

            if (index >= query.Length)
            {
                break;
            }

            if (query[index] == ';')
            {
                if (query[(index + 1)..].Trim().Length == 0)
                {
                    break;
                }

                throw new QueryCanvasException("Only a trailing semicolon is supported in query canvas expressions.");
            }

            if (query[index] != '.')
            {
                throw new QueryCanvasException($"Unexpected token near '{query[index..Math.Min(query.Length, index + 16)]}'. Query operations must be chained with '.'.");
            }

            index++;
            var methodStart = index;
            while (index < query.Length && (char.IsLetterOrDigit(query[index]) || query[index] == '_'))
            {
                index++;
            }

            if (methodStart == index)
            {
                throw new QueryCanvasException("Expected a query operation name after '.'.");
            }

            var methodName = query[methodStart..index];
            while (index < query.Length && char.IsWhiteSpace(query[index]))
            {
                index++;
            }

            if (index >= query.Length || query[index] != '(')
            {
                throw new QueryCanvasException($"Expected '(' after {methodName}.");
            }

            var argumentStart = ++index;
            var depth = 1;
            var inString = false;
            var escape = false;
            while (index < query.Length && depth > 0)
            {
                var ch = query[index];
                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                    }
                    else if (ch == '\\')
                    {
                        escape = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }
                }
                else if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                }

                index++;
            }

            if (depth != 0)
            {
                throw new QueryCanvasException($"Unclosed argument list for {methodName}.");
            }

            operations.Add(new QueryOperation(methodName, query[argumentStart..(index - 1)]));
        }

        return new QueryPipeline(root, operations.ToImmutable());
    }

    private static QueryRoot ReadRoot(string query)
    {
        var index = 0;
        while (index < query.Length && char.IsWhiteSpace(query[index]))
        {
            index++;
        }

        var start = index;
        while (index < query.Length && (char.IsLetterOrDigit(query[index]) || query[index] == '_'))
        {
            index++;
        }

        if (start == index)
        {
            throw new QueryCanvasException("Query must start with a root such as Files, WorkspaceFiles, or Symbols.");
        }

        return new QueryRoot(query[start..index]);
    }
}

internal enum QueryEntityKind
{
    Document,
    Symbol
}

internal sealed record QueryEntity(
    QueryEntityKind Kind,
    DiffDocumentSnapshot? Document,
    SemanticNavigationItem? Symbol,
    QueryFileMetrics? FileMetrics)
{
    public static QueryEntity FromDocument(DiffDocumentSnapshot document, QueryFileMetrics? metrics = null) =>
        new(QueryEntityKind.Document, document, null, metrics);

    public static QueryEntity FromSymbol(SemanticNavigationItem symbol) =>
        new(QueryEntityKind.Symbol, null, symbol, null);
}

internal static class QueryPredicateEvaluator
{
    private static readonly string[] Comparators = ["==", "!=", ">=", "<=", ">", "<"];

    public static bool Evaluate(QueryEntity row, string argument)
    {
        var expression = ExtractLambdaBody(argument);
        var orTerms = SplitTopLevel(expression, "||");
        foreach (var orTerm in orTerms)
        {
            var andTerms = SplitTopLevel(orTerm, "&&");
            var all = true;
            foreach (var andTerm in andTerms)
            {
                if (!EvaluateCondition(row, andTerm.Trim()))
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    public static string ExtractLambdaBody(string argument)
    {
        var trimmed = argument.Trim();
        var arrowIndex = trimmed.IndexOf("=>", StringComparison.Ordinal);
        if (arrowIndex >= 0)
        {
            trimmed = trimmed[(arrowIndex + 2)..].Trim();
        }

        return TrimOuterParentheses(trimmed);
    }

    public static object? GetPropertyValue(QueryEntity row, string propertyExpression)
    {
        var property = NormalizePropertyExpression(propertyExpression);
        if (row.Document is { } document)
        {
            return GetDocumentProperty(document, row.FileMetrics, property);
        }

        if (row.Symbol is { } symbol)
        {
            return GetSymbolProperty(symbol, property);
        }

        return null;
    }

    private static bool EvaluateCondition(QueryEntity row, string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        condition = TrimOuterParentheses(condition.Trim());
        if (condition.StartsWith('!'))
        {
            return !EvaluateCondition(row, condition[1..].Trim());
        }

        if (TryEvaluateStringMethod(row, condition, out var stringMethodResult))
        {
            return stringMethodResult;
        }

        foreach (var comparator in Comparators)
        {
            var index = IndexOfTopLevel(condition, comparator);
            if (index < 0)
            {
                continue;
            }

            var left = condition[..index].Trim();
            var right = condition[(index + comparator.Length)..].Trim();
            return CompareValues(GetPropertyValue(row, left), ParseLiteral(right), comparator);
        }

        return ToBool(GetPropertyValue(row, condition));
    }

    private static bool TryEvaluateStringMethod(QueryEntity row, string condition, out bool result)
    {
        foreach (var methodName in new[] { "Contains", "StartsWith", "EndsWith" })
        {
            var marker = $".{methodName}(";
            var markerIndex = condition.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0 || !condition.EndsWith(')'))
            {
                continue;
            }

            var property = condition[..markerIndex].Trim();
            var argument = condition[(markerIndex + marker.Length)..^1].Trim();
            var value = Convert.ToString(GetPropertyValue(row, property)) ?? string.Empty;
            var needle = Convert.ToString(ParseLiteral(argument)) ?? string.Empty;
            result = methodName switch
            {
                "StartsWith" => value.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
                "EndsWith" => value.EndsWith(needle, StringComparison.OrdinalIgnoreCase),
                _ => value.Contains(needle, StringComparison.OrdinalIgnoreCase)
            };
            return true;
        }

        result = false;
        return false;
    }

    private static bool CompareValues(object? left, object? right, string comparator)
    {
        if (TryToDouble(left, out var leftNumber) && TryToDouble(right, out var rightNumber))
        {
            return comparator switch
            {
                "==" => Math.Abs(leftNumber - rightNumber) < double.Epsilon,
                "!=" => Math.Abs(leftNumber - rightNumber) >= double.Epsilon,
                ">=" => leftNumber >= rightNumber,
                "<=" => leftNumber <= rightNumber,
                ">" => leftNumber > rightNumber,
                "<" => leftNumber < rightNumber,
                _ => false
            };
        }

        if (left is bool leftBool || right is bool)
        {
            var comparison = ToBool(left).CompareTo(ToBool(right));
            return CompareComparison(comparison, comparator);
        }

        var stringComparison = string.Compare(Convert.ToString(left), Convert.ToString(right), StringComparison.OrdinalIgnoreCase);
        return CompareComparison(stringComparison, comparator);
    }

    private static bool CompareComparison(int comparison, string comparator) => comparator switch
    {
        "==" => comparison == 0,
        "!=" => comparison != 0,
        ">=" => comparison >= 0,
        "<=" => comparison <= 0,
        ">" => comparison > 0,
        "<" => comparison < 0,
        _ => false
    };

    private static object? ParseLiteral(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
        {
            return value[1..^1].Replace("\\\"", "\"");
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(value, out var intValue))
        {
            return intValue;
        }

        if (double.TryParse(value, out var doubleValue))
        {
            return doubleValue;
        }

        return value.Trim('\'');
    }

    private static object? GetDocumentProperty(DiffDocumentSnapshot document, QueryFileMetrics? metrics, string property) => property.ToLowerInvariant() switch
    {
        "id" => document.Id.Value,
        "path" => document.Metadata.Path,
        "name" => Path.GetFileName(document.Metadata.Path),
        "directory" => Path.GetDirectoryName(document.Metadata.Path)?.Replace('\\', '/') ?? string.Empty,
        "oldpath" => document.Metadata.OldPath ?? string.Empty,
        "language" => document.Metadata.Language,
        "status" => document.Metadata.Status.ToString(),
        "addedlines" or "added" => document.Metadata.AddedLines,
        "deletedlines" or "deleted" => document.Metadata.DeletedLines,
        "linecount" or "lines" => metrics?.LineCount ?? document.LineCount,
        "sizebytes" or "filesize" or "bytes" => metrics?.SizeBytes ?? EstimateDocumentSize(document),
        "sizekb" or "kilobytes" => (metrics?.SizeBytes ?? EstimateDocumentSize(document)) / 1024d,
        "ischanged" => document.Metadata.Status != DiffFileStatus.Unchanged || document.Metadata.AddedLines > 0 || document.Metadata.DeletedLines > 0,
        "isadded" => document.Metadata.Status == DiffFileStatus.Added,
        "isdeleted" => document.Metadata.Status == DiffFileStatus.Deleted,
        _ => throw new QueryCanvasException($"Unknown file property '{property}'. Try Path, Name, Directory, Language, Status, AddedLines, DeletedLines, LineCount, SizeBytes, or IsChanged.")
    };

    private static long EstimateDocumentSize(DiffDocumentSnapshot document)
    {
        if (document.Lines.IsDefaultOrEmpty)
        {
            return 0;
        }

        long size = 0;
        foreach (var line in document.Lines)
        {
            size += line.Text.Length + 1L;
        }

        return size;
    }

    private static object? GetSymbolProperty(SemanticNavigationItem symbol, string property) => property.ToLowerInvariant() switch
    {
        "id" or "anchorid" => symbol.AnchorId,
        "documentid" => symbol.DocumentId.Value,
        "path" or "file" => symbol.Path,
        "name" or "displayname" => symbol.DisplayName,
        "kind" or "kindtext" => symbol.KindText,
        "line" => symbol.Line,
        "column" => symbol.Column,
        "links" or "incidentedgecount" => symbol.IncidentEdgeCount,
        "ischanged" => symbol.IsChanged,
        "islinked" => symbol.IsLinked,
        _ => throw new QueryCanvasException($"Unknown symbol property '{property}'. Try Name, Kind, Path, Line, Column, IsChanged, IsLinked, or Links.")
    };

    private static string NormalizePropertyExpression(string expression)
    {
        var property = ExtractLambdaBody(expression).Trim();
        var dotIndex = property.IndexOf('.');
        if (dotIndex >= 0 && dotIndex + 1 < property.Length)
        {
            property = property[(dotIndex + 1)..];
        }

        return TrimOuterParentheses(property).Trim();
    }

    private static bool ToBool(object? value) => value switch
    {
        bool boolValue => boolValue,
        int intValue => intValue != 0,
        long longValue => longValue != 0,
        double doubleValue => Math.Abs(doubleValue) > double.Epsilon,
        string stringValue => !string.IsNullOrWhiteSpace(stringValue) && !string.Equals(stringValue, "false", StringComparison.OrdinalIgnoreCase),
        _ => value is not null
    };

    private static bool TryToDouble(object? value, out double number)
    {
        switch (value)
        {
            case byte or short or int or long or float or double or decimal:
                number = Convert.ToDouble(value);
                return true;
            case string text when double.TryParse(text, out var parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static int IndexOfTopLevel(string text, string token)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var index = 0; index <= text.Length - token.Length; index++)
        {
            var ch = text[index];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && text.AsSpan(index).StartsWith(token, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> SplitTopLevel(string text, string separator)
    {
        var parts = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text[start..];
            var index = IndexOfTopLevel(remaining, separator);
            if (index < 0)
            {
                parts.Add(remaining);
                break;
            }

            parts.Add(remaining[..index]);
            start += index + separator.Length;
        }

        return parts.Count == 0 ? [text] : parts;
    }

    private static string TrimOuterParentheses(string value)
    {
        value = value.Trim();
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')')
        {
            var inner = value[1..^1];
            if (IndexOfTopLevel(inner, "||") >= 0 || IndexOfTopLevel(inner, "&&") >= 0 || Balanced(inner))
            {
                value = inner.Trim();
                continue;
            }

            break;
        }

        return value;
    }

    private static bool Balanced(string value)
    {
        var depth = 0;
        foreach (var ch in value)
        {
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }
}

internal sealed class QueryValueComparer : IComparer<object?>
{
    public static QueryValueComparer Instance { get; } = new();

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (TryDouble(x, out var left) && TryDouble(y, out var right))
        {
            return left.CompareTo(right);
        }

        if (x is bool leftBool && y is bool rightBool)
        {
            return leftBool.CompareTo(rightBool);
        }

        return string.Compare(Convert.ToString(x), Convert.ToString(y), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDouble(object value, out double number)
    {
        switch (value)
        {
            case byte or short or int or long or float or double or decimal:
                number = Convert.ToDouble(value);
                return true;
            case string text when double.TryParse(text, out var parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }
}
