using SemanticDiff.Core;
using SkiaSharp;

namespace SemanticDiff.Controls.Uno;

internal static class CodeTextStyleMap
{
    public static bool IsBoldToken(TokenSpan token) =>
        token.TokenType is "keyword" or "class" or "interface" or "enum" or "struct" or "function" or "method" ||
        (!token.Modifiers.IsDefaultOrEmpty &&
            token.Modifiers.Any(modifier => string.Equals(modifier, "declaration", StringComparison.OrdinalIgnoreCase)));

    public static SKColor TokenColor(TokenSpan token, CodeFileViewerPalette palette)
    {
        var style = string.IsNullOrWhiteSpace(token.StyleId) || token.StyleId == "text"
            ? token.TokenType
            : token.StyleId;

        return style switch
        {
            "keyword" or "operator" => palette.Keyword,
            "type" or "namespace" or "class" or "interface" or "enum" or "struct" or "typeParameter" => palette.Type,
            "string" or "regexp" => palette.String,
            "comment" => palette.Comment,
            "number" => palette.Number,
            "function" or "method" => palette.Function,
            "property" or "enumMember" or "event" => palette.Property,
            "parameter" => palette.Parameter,
            "variable" => palette.Text,
            "tag" or "decorator" or "macro" => palette.Tag,
            "punctuation" => palette.Punctuation,
            "invalid" => palette.Invalid,
            _ => palette.Text
        };
    }

    public static string MarkerFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Deleted => "-",
        DiffLineKind.Modified => "*",
        DiffLineKind.Ignored => "~",
        DiffLineKind.Moved => ">",
        DiffLineKind.Conflict => "!",
        DiffLineKind.Metadata => "@",
        DiffLineKind.Imaginary => "...",
        _ => string.Empty
    };

    public static SKColor LineAccentColor(DiffLineKind kind, CodeFileViewerPalette palette) => kind switch
    {
        DiffLineKind.Added => palette.AddedAccent,
        DiffLineKind.Deleted => palette.DeletedAccent,
        DiffLineKind.Modified => palette.ModifiedAccent,
        DiffLineKind.Moved => palette.MovedAccent,
        DiffLineKind.Conflict => palette.ConflictAccent,
        DiffLineKind.Metadata => palette.MetadataAccent,
        DiffLineKind.Imaginary => palette.FoldText,
        _ => palette.LineNumber
    };
}
