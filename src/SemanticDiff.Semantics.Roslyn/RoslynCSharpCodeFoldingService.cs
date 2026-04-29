using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SemanticDiff.Core;

namespace SemanticDiff.Semantics.Roslyn;

public sealed class RoslynCSharpCodeFoldingService
{
    private const int TabSize = 4;

    public static bool CanFold(DiffDocumentSnapshot document)
    {
        var language = (document.Metadata.Language ?? string.Empty).Trim();
        return language.Equals("C#", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("csharp", StringComparison.OrdinalIgnoreCase) ||
            document.Metadata.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    public ImmutableArray<CodeFoldRegion> CreateFoldRegions(DiffDocumentSnapshot document, CancellationToken cancellationToken = default)
    {
        if (document.Lines.IsDefaultOrEmpty || !CanFold(document))
        {
            return [];
        }

        var sourceText = SourceText.From(document.ToSourceText());
        var tree = CSharpSyntaxTree.ParseText(
            sourceText,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            document.Metadata.Path,
            cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var builder = ImmutableArray.CreateBuilder<CodeFoldRegion>();

        AddRegionDirectiveFoldings(document, builder);
        foreach (var node in root.DescendantNodes(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSyntaxFoldRegion(document, sourceText, node, builder);
        }

        return builder
            .Where(region => region.EndLineIndex > region.StartLineIndex)
            .GroupBy(region => region.StartLineIndex)
            .Select(group => group.OrderByDescending(region => region.EndLineIndex).First())
            .OrderBy(region => region.StartLineIndex)
            .ThenByDescending(region => region.EndLineIndex)
            .ToImmutableArray();
    }

    private static void AddSyntaxFoldRegion(
        DiffDocumentSnapshot document,
        SourceText sourceText,
        SyntaxNode node,
        ImmutableArray<CodeFoldRegion>.Builder builder)
    {
        if (!TryGetStructuralFold(node, out var startNode, out var openBrace, out var closeBrace))
        {
            return;
        }

        if (openBrace.IsMissing || closeBrace.IsMissing)
        {
            return;
        }

        var startLine = sourceText.Lines.GetLinePosition(startNode.SpanStart).Line;
        var endLine = sourceText.Lines.GetLinePosition(closeBrace.SpanStart).Line;
        if (startLine < 0 || endLine <= startLine || startLine >= document.Lines.Length)
        {
            return;
        }

        var guideLine = sourceText.Lines.GetLinePosition(openBrace.SpanStart).Line;
        if (guideLine < 0 || guideLine >= document.Lines.Length)
        {
            return;
        }

        builder.Add(new CodeFoldRegion(
            startLine,
            Math.Min(endLine, document.Lines.Length - 1),
            CreateTitle(document.Lines[startLine].Text),
            guideLine,
            GetVisualColumn(document.Lines[guideLine].Text, sourceText.Lines.GetLinePosition(openBrace.SpanStart).Character)));
    }

    private static bool TryGetStructuralFold(
        SyntaxNode node,
        out SyntaxNode startNode,
        out SyntaxToken openBrace,
        out SyntaxToken closeBrace)
    {
        startNode = node;
        openBrace = default;
        closeBrace = default;

        switch (node)
        {
            case NamespaceDeclarationSyntax namespaceDeclaration:
                openBrace = namespaceDeclaration.OpenBraceToken;
                closeBrace = namespaceDeclaration.CloseBraceToken;
                return true;

            case TypeDeclarationSyntax typeDeclaration:
                openBrace = typeDeclaration.OpenBraceToken;
                closeBrace = typeDeclaration.CloseBraceToken;
                return true;

            case EnumDeclarationSyntax enumDeclaration:
                openBrace = enumDeclaration.OpenBraceToken;
                closeBrace = enumDeclaration.CloseBraceToken;
                return true;

            case BaseMethodDeclarationSyntax { Body: { } body }:
                openBrace = body.OpenBraceToken;
                closeBrace = body.CloseBraceToken;
                return true;

            case LocalFunctionStatementSyntax { Body: { } body } localFunction:
                startNode = localFunction;
                openBrace = body.OpenBraceToken;
                closeBrace = body.CloseBraceToken;
                return true;

            case AccessorDeclarationSyntax { Body: { } body } accessor:
                startNode = accessor;
                openBrace = body.OpenBraceToken;
                closeBrace = body.CloseBraceToken;
                return true;

            case PropertyDeclarationSyntax { AccessorList: { } accessorList }:
                openBrace = accessorList.OpenBraceToken;
                closeBrace = accessorList.CloseBraceToken;
                return true;

            case IndexerDeclarationSyntax { AccessorList: { } accessorList }:
                openBrace = accessorList.OpenBraceToken;
                closeBrace = accessorList.CloseBraceToken;
                return true;

            case EventDeclarationSyntax { AccessorList: { } accessorList }:
                openBrace = accessorList.OpenBraceToken;
                closeBrace = accessorList.CloseBraceToken;
                return true;

            case SwitchStatementSyntax switchStatement:
                openBrace = switchStatement.OpenBraceToken;
                closeBrace = switchStatement.CloseBraceToken;
                return true;

            case BlockSyntax block when IsStandaloneStructuralBlock(block):
                startNode = block.Parent ?? block;
                openBrace = block.OpenBraceToken;
                closeBrace = block.CloseBraceToken;
                return true;

            default:
                return false;
        }
    }

    private static bool IsStandaloneStructuralBlock(BlockSyntax block) =>
        block.Parent is not BaseMethodDeclarationSyntax and
            not LocalFunctionStatementSyntax and
            not AccessorDeclarationSyntax and
            not AnonymousFunctionExpressionSyntax;

    private static void AddRegionDirectiveFoldings(DiffDocumentSnapshot document, ImmutableArray<CodeFoldRegion>.Builder builder)
    {
        var stack = new Stack<(int LineIndex, string Title)>();
        foreach (var line in document.Lines)
        {
            var trimmed = line.Text.Trim();
            if (trimmed.StartsWith("#region", StringComparison.Ordinal))
            {
                var title = trimmed.Length > "#region".Length ? trimmed["#region".Length..].Trim() : "#region";
                stack.Push((line.Index, string.IsNullOrWhiteSpace(title) ? "#region" : title));
            }
            else if (trimmed.StartsWith("#endregion", StringComparison.Ordinal) && stack.TryPop(out var start))
            {
                builder.Add(new CodeFoldRegion(start.LineIndex, line.Index, start.Title));
            }
        }
    }

    private static int GetVisualColumn(string text, int column)
    {
        var visualColumn = 0;
        var boundedColumn = Math.Clamp(column, 0, text.Length);
        for (var index = 0; index < boundedColumn; index++)
        {
            visualColumn += text[index] == '\t' ? TabSize : 1;
        }

        return visualColumn;
    }

    private static string CreateTitle(string text)
    {
        var title = text.Trim();
        return title.Length <= 80 ? title : $"{title[..77]}...";
    }
}
