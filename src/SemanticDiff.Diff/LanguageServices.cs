using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

internal sealed class LanguageDefinition
{
    public LanguageDefinition(
        string id,
        IEnumerable<string> aliases,
        IEnumerable<string> extensions,
        IEnumerable<string> keywords,
        IEnumerable<string>? lineCommentPrefixes = null,
        IEnumerable<(string Open, string Close)>? blockComments = null,
        LanguageSyntaxKind syntaxKind = LanguageSyntaxKind.Code,
        IEnumerable<string>? stringDelimiters = null,
        IEnumerable<string>? fileNames = null)
    {
        Id = id;
        Aliases = aliases.Append(id).Select(Normalize).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        Extensions = extensions.Select(NormalizeExtension).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        Keywords = keywords.ToImmutableHashSet(StringComparer.Ordinal);
        LineCommentPrefixes = (lineCommentPrefixes ?? ["//"]).ToImmutableArray();
        BlockComments = (blockComments ?? [("/*", "*/")]).ToImmutableArray();
        SyntaxKind = syntaxKind;
        StringDelimiters = (stringDelimiters ?? ["\"", "'", "`"]).ToImmutableArray();
        FileNames = (fileNames ?? []).Select(Normalize).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; }

    public ImmutableHashSet<string> Aliases { get; }

    public ImmutableHashSet<string> Extensions { get; }

    public ImmutableHashSet<string> Keywords { get; }

    public ImmutableArray<string> LineCommentPrefixes { get; }

    public ImmutableArray<(string Open, string Close)> BlockComments { get; }

    public LanguageSyntaxKind SyntaxKind { get; }

    public ImmutableArray<string> StringDelimiters { get; }

    public ImmutableHashSet<string> FileNames { get; }

    private static string NormalizeExtension(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed.StartsWith(".", StringComparison.Ordinal) ? trimmed : $".{trimmed}";
    }

    internal static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

internal enum LanguageSyntaxKind
{
    Code,
    Xml,
    Json,
    Yaml,
    Css,
    Markdown
}

internal sealed record LanguageDescriptor(string Id, string Extension, LanguageDefinition? Definition);

internal static class LanguageServiceRegistry
{
    private static readonly ImmutableArray<LanguageDefinition> Definitions =
    [
        new(
            "csharp",
            ["c#", "cs"],
            [".cs", ".csx"],
            [
                "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
                "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double",
                "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
                "foreach", "global", "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
                "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
                "params", "partial", "private", "protected", "public", "readonly", "record", "ref", "return",
                "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
                "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
                "using", "var", "virtual", "void", "volatile", "while", "with", "yield"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")],
            stringDelimiters: ["\"", "'"]),
        new(
            "xml",
            ["xaml", "axaml", "props", "targets", "resx"],
            [".xml", ".xaml", ".axaml", ".props", ".targets", ".resx", ".config"],
            [],
            lineCommentPrefixes: [],
            blockComments: [("<!--", "-->")],
            syntaxKind: LanguageSyntaxKind.Xml),
        new(
            "html",
            ["htm"],
            [".html", ".htm", ".cshtml", ".razor"],
            [],
            lineCommentPrefixes: [],
            blockComments: [("<!--", "-->")],
            syntaxKind: LanguageSyntaxKind.Xml),
        new(
            "json",
            ["jsonc"],
            [".json", ".jsonc", ".sarif"],
            ["true", "false", "null"],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")],
            syntaxKind: LanguageSyntaxKind.Json,
            stringDelimiters: ["\""]),
        new(
            "yaml",
            ["yml"],
            [".yaml", ".yml"],
            ["true", "false", "null", "yes", "no", "on", "off"],
            lineCommentPrefixes: ["#"],
            blockComments: [],
            syntaxKind: LanguageSyntaxKind.Yaml,
            stringDelimiters: ["\"", "'"]),
        new(
            "toml",
            [],
            [".toml"],
            ["true", "false"],
            lineCommentPrefixes: ["#"],
            blockComments: [],
            syntaxKind: LanguageSyntaxKind.Yaml,
            stringDelimiters: ["\"", "'"]),
        new(
            "ini",
            ["properties"],
            [".ini", ".editorconfig", ".properties"],
            ["true", "false", "yes", "no", "on", "off"],
            lineCommentPrefixes: ["#", ";"],
            blockComments: [],
            syntaxKind: LanguageSyntaxKind.Yaml,
            stringDelimiters: ["\"", "'"]),
        new(
            "markdown",
            ["md"],
            [".md", ".markdown", ".mdx"],
            [],
            lineCommentPrefixes: [],
            blockComments: [("<!--", "-->")],
            syntaxKind: LanguageSyntaxKind.Markdown),
        new(
            "javascript",
            ["js", "javascriptreact", "jsx", "node"],
            [".js", ".jsx", ".mjs", ".cjs"],
            [
                "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
                "delete", "do", "else", "export", "extends", "false", "finally", "for", "from", "function",
                "get", "if", "import", "in", "instanceof", "let", "new", "null", "of", "return", "set",
                "static", "super", "switch", "this", "throw", "true", "try", "typeof", "undefined", "var",
                "void", "while", "with", "yield"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")]),
        new(
            "typescript",
            ["ts", "typescriptreact", "tsx"],
            [".ts", ".tsx"],
            [
                "abstract", "any", "as", "async", "await", "boolean", "break", "case", "catch", "class",
                "const", "constructor", "continue", "debugger", "declare", "default", "delete", "do", "else",
                "enum", "export", "extends", "false", "finally", "for", "from", "function", "get", "if",
                "implements", "import", "in", "infer", "instanceof", "interface", "keyof", "let", "module",
                "namespace", "never", "new", "null", "number", "object", "of", "private", "protected",
                "public", "readonly", "return", "set", "static", "string", "super", "switch", "symbol",
                "this", "throw", "true", "try", "type", "typeof", "undefined", "unknown", "var", "void",
                "while", "with", "yield"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")]),
        new(
            "css",
            ["scss", "sass", "less"],
            [".css", ".scss", ".sass", ".less"],
            [
                "@charset", "@container", "@font-face", "@import", "@keyframes", "@media", "@namespace",
                "@page", "@supports", "important", "inherit", "initial", "unset", "var"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")],
            syntaxKind: LanguageSyntaxKind.Css),
        new(
            "python",
            ["py"],
            [".py", ".pyw"],
            [
                "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
                "else", "except", "False", "finally", "for", "from", "global", "if", "import", "in", "is",
                "lambda", "None", "nonlocal", "not", "or", "pass", "raise", "return", "True", "try",
                "while", "with", "yield"
            ],
            lineCommentPrefixes: ["#"],
            blockComments: [("\"\"\"", "\"\"\""), ("'''", "'''")],
            stringDelimiters: ["\"", "'", "\"\"\"", "'''"]),
        new(
            "ruby",
            ["rb"],
            [".rb"],
            [
                "alias", "and", "begin", "break", "case", "class", "def", "defined?", "do", "else", "elsif",
                "end", "ensure", "false", "for", "if", "in", "module", "next", "nil", "not", "or", "redo",
                "rescue", "retry", "return", "self", "super", "then", "true", "undef", "unless", "until",
                "when", "while", "yield"
            ],
            lineCommentPrefixes: ["#"],
            blockComments: [("=begin", "=end")]),
        new(
            "go",
            ["golang"],
            [".go"],
            [
                "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough",
                "for", "func", "go", "goto", "if", "import", "interface", "map", "nil", "package", "range",
                "return", "select", "struct", "switch", "type", "var"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")]),
        new(
            "rust",
            ["rs"],
            [".rs"],
            [
                "as", "async", "await", "box", "break", "const", "continue", "crate", "dyn", "else", "enum",
                "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod", "move",
                "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super", "trait", "true",
                "type", "unsafe", "use", "where", "while"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")]),
        new(
            "java",
            [],
            [".java"],
            [
                "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
                "continue", "default", "do", "double", "else", "enum", "extends", "false", "final", "finally",
                "float", "for", "goto", "if", "implements", "import", "instanceof", "int", "interface",
                "long", "native", "new", "null", "package", "private", "protected", "public", "return",
                "short", "static", "strictfp", "super", "switch", "synchronized", "this", "throw", "throws",
                "transient", "true", "try", "void", "volatile", "while"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")]),
        new(
            "kotlin",
            ["kt"],
            [".kt", ".kts"],
            [
                "as", "break", "class", "continue", "do", "else", "false", "for", "fun", "if", "in",
                "interface", "is", "null", "object", "package", "return", "super", "this", "throw", "true",
                "try", "typealias", "typeof", "val", "var", "when", "while"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")]),
        new(
            "cpp",
            ["c", "c++", "cc", "h", "hpp", "objc", "objective-c"],
            [".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".m", ".mm"],
            [
                "alignas", "alignof", "asm", "auto", "bool", "break", "case", "catch", "char", "class",
                "const", "constexpr", "continue", "decltype", "default", "delete", "do", "double",
                "else", "enum", "explicit", "export", "extern", "false", "float", "for", "friend", "goto",
                "if", "inline", "int", "long", "namespace", "new", "noexcept", "nullptr", "operator",
                "private", "protected", "public", "register", "return", "short", "signed", "sizeof",
                "static", "struct", "switch", "template", "this", "throw", "true", "try", "typedef",
                "typename", "union", "unsigned", "using", "virtual", "void", "volatile", "while"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")]),
        new(
            "php",
            [],
            [".php", ".phtml"],
            [
                "abstract", "and", "array", "as", "break", "callable", "case", "catch", "class", "clone",
                "const", "continue", "declare", "default", "die", "do", "echo", "else", "elseif", "empty",
                "enddeclare", "endfor", "endforeach", "endif", "endswitch", "endwhile", "eval", "exit",
                "extends", "false", "final", "finally", "fn", "for", "foreach", "function", "global",
                "goto", "if", "implements", "include", "include_once", "instanceof", "insteadof",
                "interface", "isset", "list", "match", "namespace", "new", "null", "or", "print", "private",
                "protected", "public", "readonly", "require", "require_once", "return", "static", "switch",
                "throw", "trait", "true", "try", "unset", "use", "var", "while", "xor", "yield"
            ],
            lineCommentPrefixes: ["//", "#"],
            blockComments: [("/*", "*/")]),
        new(
            "shellscript",
            ["shell", "bash", "zsh", "sh"],
            [".sh", ".bash", ".zsh", ".profile", ".bashrc", ".zshrc"],
            [
                "case", "do", "done", "elif", "else", "esac", "export", "fi", "for", "function", "if",
                "in", "local", "readonly", "return", "select", "then", "until", "while"
            ],
            lineCommentPrefixes: ["#"],
            blockComments: [],
            stringDelimiters: ["\"", "'", "`"]),
        new(
            "powershell",
            ["ps1", "pwsh"],
            [".ps1", ".psm1", ".psd1"],
            [
                "begin", "break", "catch", "class", "clean", "continue", "data", "define", "do", "dynamicparam",
                "else", "elseif", "end", "enum", "exit", "filter", "finally", "for", "foreach", "from",
                "function", "hidden", "if", "in", "param", "process", "return", "static", "switch", "throw",
                "trap", "try", "until", "using", "var", "while"
            ],
            lineCommentPrefixes: ["#"],
            blockComments: [("<#", "#>")],
            stringDelimiters: ["\"", "'"]),
        new(
            "sql",
            [],
            [".sql"],
            [
                "ADD", "ALTER", "AND", "AS", "ASC", "BEGIN", "BETWEEN", "BY", "CASE", "CREATE", "DELETE",
                "DESC", "DISTINCT", "DROP", "ELSE", "END", "EXISTS", "FALSE", "FROM", "GROUP", "HAVING",
                "IN", "INDEX", "INNER", "INSERT", "INTO", "IS", "JOIN", "LEFT", "LIKE", "LIMIT", "NOT",
                "NULL", "ON", "OR", "ORDER", "OUTER", "PRIMARY", "RIGHT", "SELECT", "SET", "TABLE",
                "THEN", "TRUE", "UNION", "UPDATE", "VALUES", "VIEW", "WHEN", "WHERE"
            ],
            lineCommentPrefixes: ["--"],
            blockComments: [("/*", "*/")],
            stringDelimiters: ["\"", "'"]),
        new(
            "dockerfile",
            ["docker"],
            [".dockerfile"],
            [
                "ADD", "ARG", "CMD", "COPY", "ENTRYPOINT", "ENV", "EXPOSE", "FROM", "HEALTHCHECK", "LABEL",
                "MAINTAINER", "ONBUILD", "RUN", "SHELL", "STOPSIGNAL", "USER", "VOLUME", "WORKDIR"
            ],
            lineCommentPrefixes: ["#"],
            blockComments: [],
            fileNames: ["Dockerfile"]),
        new(
            "swift",
            [],
            [".swift"],
            [
                "Any", "as", "associatedtype", "break", "case", "catch", "class", "continue", "default",
                "defer", "deinit", "do", "else", "enum", "extension", "false", "fileprivate", "for", "func",
                "guard", "if", "import", "in", "init", "inout", "internal", "is", "let", "nil", "open",
                "operator", "private", "protocol", "public", "repeat", "return", "self", "Self", "static",
                "struct", "subscript", "super", "switch", "throw", "throws", "true", "try", "typealias",
                "var", "where", "while"
            ],
            lineCommentPrefixes: ["//"],
            blockComments: [("/*", "*/")])
    ];

    private static readonly ImmutableDictionary<string, LanguageDefinition> DefinitionsByExtension = Definitions
        .SelectMany(definition => definition.Extensions.Select(extension => (extension, definition)))
        .GroupBy(item => item.extension, StringComparer.OrdinalIgnoreCase)
        .ToImmutableDictionary(group => group.Key, group => group.First().definition, StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableDictionary<string, LanguageDefinition> DefinitionsByAlias = Definitions
        .SelectMany(definition => definition.Aliases.Select(alias => (alias, definition)))
        .GroupBy(item => item.alias, StringComparer.OrdinalIgnoreCase)
        .ToImmutableDictionary(group => group.Key, group => group.First().definition, StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableDictionary<string, LanguageDefinition> DefinitionsByFileName = Definitions
        .SelectMany(definition => definition.FileNames.Select(fileName => (fileName, definition)))
        .GroupBy(item => item.fileName, StringComparer.OrdinalIgnoreCase)
        .ToImmutableDictionary(group => group.Key, group => group.First().definition, StringComparer.OrdinalIgnoreCase);

    public static LanguageDescriptor Identify(DiffDocumentSnapshot document)
    {
        var extension = Path.GetExtension(document.Metadata.Path).ToLowerInvariant();
        var fileName = LanguageDefinition.Normalize(Path.GetFileName(document.Metadata.Path));
        var language = LanguageDefinition.Normalize(document.Metadata.Language);

        var definition = DefinitionsByFileName.GetValueOrDefault(fileName) ??
            DefinitionsByExtension.GetValueOrDefault(extension) ??
            DefinitionsByAlias.GetValueOrDefault(language);

        var id = definition?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = string.IsNullOrWhiteSpace(language) ? "plaintext" : language;
        }

        return new LanguageDescriptor(id, extension, definition);
    }

    public static IEnumerable<string> GetLanguageIdCandidates(DiffDocumentSnapshot document)
    {
        var descriptor = Identify(document);
        if (!string.IsNullOrWhiteSpace(descriptor.Id))
        {
            yield return descriptor.Id;
        }

        if (descriptor.Definition is not null)
        {
            foreach (var alias in descriptor.Definition.Aliases)
            {
                if (!string.Equals(alias, descriptor.Id, StringComparison.OrdinalIgnoreCase))
                {
                    yield return alias;
                }
            }
        }

        var normalizedLanguage = LanguageDefinition.Normalize(document.Metadata.Language);
        if (!string.IsNullOrWhiteSpace(normalizedLanguage) &&
            !string.Equals(normalizedLanguage, descriptor.Id, StringComparison.OrdinalIgnoreCase))
        {
            yield return normalizedLanguage;
        }
    }
}

internal static class TokenClassification
{
    public const string TextMateSource = "textmate";
    public const string FallbackSource = "fallback";

    public static TokenSpan Create(
        int startColumn,
        int length,
        string styleId,
        string tokenType,
        string languageId,
        string source,
        ImmutableArray<string> modifiers = default,
        ImmutableArray<string> scopes = default)
    {
        return new TokenSpan(startColumn, length, styleId, tokenType, modifiers, scopes, languageId, source);
    }

    public static (string StyleId, string TokenType, ImmutableArray<string> Modifiers) FromScopes(IReadOnlyList<string> scopes)
    {
        var modifiers = ImmutableArray.CreateBuilder<string>();
        var styleId = "text";
        var tokenType = "text";

        for (var scopeIndex = scopes.Count - 1; scopeIndex >= 0; scopeIndex--)
        {
            var scope = scopes[scopeIndex];
            AddScopeModifiers(scope, modifiers);

            if (styleId != "text")
            {
                continue;
            }

            (styleId, tokenType) = MapScope(scope);
        }

        return (styleId, tokenType, modifiers.Count == 0 ? ImmutableArray<string>.Empty : modifiers.ToImmutable());
    }

    public static string StyleFromTokenType(string tokenType) => tokenType switch
    {
        "namespace" or "class" or "enum" or "interface" or "struct" or "typeParameter" or "type" => "type",
        "function" or "method" => "function",
        "parameter" or "property" or "enumMember" or "event" => "property",
        "decorator" or "macro" or "label" => "tag",
        "comment" => "comment",
        "string" or "regexp" => "string",
        "keyword" => "keyword",
        "number" => "number",
        "operator" => "operator",
        "variable" => "variable",
        "invalid" => "invalid",
        _ => "text"
    };

    private static (string StyleId, string TokenType) MapScope(string scope)
    {
        if (scope.Contains("invalid", StringComparison.Ordinal))
        {
            return ("invalid", "invalid");
        }

        if (scope.Contains("comment", StringComparison.Ordinal))
        {
            return ("comment", "comment");
        }

        if (scope.Contains("string.regexp", StringComparison.Ordinal))
        {
            return ("string", "regexp");
        }

        if (scope.Contains("string", StringComparison.Ordinal))
        {
            return ("string", "string");
        }

        if (scope.Contains("constant.numeric", StringComparison.Ordinal))
        {
            return ("number", "number");
        }

        if (scope.Contains("constant.language", StringComparison.Ordinal))
        {
            return ("number", "keyword");
        }

        if (scope.Contains("keyword.operator", StringComparison.Ordinal))
        {
            return ("operator", "operator");
        }

        if (scope.Contains("keyword", StringComparison.Ordinal) || scope.Contains("storage", StringComparison.Ordinal))
        {
            return ("keyword", "keyword");
        }

        if (scope.Contains("entity.name.namespace", StringComparison.Ordinal))
        {
            return ("type", "namespace");
        }

        if (scope.Contains("entity.name.function", StringComparison.Ordinal) || scope.Contains("support.function", StringComparison.Ordinal))
        {
            return ("function", "function");
        }

        if (scope.Contains("entity.name.type.class", StringComparison.Ordinal) || scope.Contains("support.class", StringComparison.Ordinal))
        {
            return ("type", "class");
        }

        if (scope.Contains("entity.name.type.interface", StringComparison.Ordinal))
        {
            return ("type", "interface");
        }

        if (scope.Contains("entity.name.type.enum", StringComparison.Ordinal))
        {
            return ("type", "enum");
        }

        if (scope.Contains("entity.name.type", StringComparison.Ordinal) || scope.Contains("support.type", StringComparison.Ordinal))
        {
            return ("type", "type");
        }

        if (scope.Contains("entity.name.tag", StringComparison.Ordinal))
        {
            return ("tag", "type");
        }

        if (scope.Contains("entity.other.attribute-name", StringComparison.Ordinal))
        {
            return ("property", "property");
        }

        if (scope.Contains("variable.parameter", StringComparison.Ordinal))
        {
            return ("property", "parameter");
        }

        if (scope.Contains("variable.other.property", StringComparison.Ordinal))
        {
            return ("property", "property");
        }

        if (scope.Contains("variable", StringComparison.Ordinal))
        {
            return ("variable", "variable");
        }

        if (scope.Contains("punctuation", StringComparison.Ordinal) || scope.Contains("meta.brace", StringComparison.Ordinal))
        {
            return ("punctuation", "operator");
        }

        return ("text", "text");
    }

    private static void AddScopeModifiers(string scope, ImmutableArray<string>.Builder modifiers)
    {
        if (scope.Contains("entity.name", StringComparison.Ordinal))
        {
            AddModifier(modifiers, "declaration");
        }

        if (scope.Contains("support.", StringComparison.Ordinal))
        {
            AddModifier(modifiers, "defaultLibrary");
        }

        if (scope.Contains("constant", StringComparison.Ordinal) || scope.Contains("readonly", StringComparison.Ordinal))
        {
            AddModifier(modifiers, "readonly");
        }

        if (scope.Contains("static", StringComparison.Ordinal))
        {
            AddModifier(modifiers, "static");
        }

        if (scope.Contains("deprecated", StringComparison.Ordinal))
        {
            AddModifier(modifiers, "deprecated");
        }

        if (scope.Contains("documentation", StringComparison.Ordinal))
        {
            AddModifier(modifiers, "documentation");
        }
    }

    private static void AddModifier(ImmutableArray<string>.Builder modifiers, string modifier)
    {
        for (var index = 0; index < modifiers.Count; index++)
        {
            if (string.Equals(modifiers[index], modifier, StringComparison.Ordinal))
            {
                return;
            }
        }

        modifiers.Add(modifier);
    }
}
