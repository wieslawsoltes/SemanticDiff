using System.Collections.Immutable;

namespace SemanticDiff.Core;

public enum FileExplorerNodeKind
{
    Folder,
    File
}

public enum FileExplorerIconKind
{
    Folder,
    File,
    CSharp,
    Xaml,
    Xml,
    Json,
    Markdown,
    Project,
    Solution,
    Config,
    Git,
    Image,
    Text,
    Binary
}

public sealed record FileExplorerFile(string Path, DiffFileStatus Status, string Language);

public sealed record FileExplorerNode(
    string Name,
    string Path,
    FileExplorerNodeKind Kind,
    DiffFileStatus Status,
    string Language,
    FileExplorerIconKind IconKind,
    string? DocumentId,
    ImmutableArray<FileExplorerNode> Children)
{
    public bool IsFile => Kind == FileExplorerNodeKind.File;

    public bool HasChildren => !Children.IsDefaultOrEmpty;

    public string SearchText => $"{Name} {Path} {Language} {Status} {IconKind}";
}

public static class FileExplorerTreeBuilder
{
    public static ImmutableArray<FileExplorerNode> Build(ImmutableArray<DiffDocumentSnapshot> documents) => Build(
        documents.Select(document => new FileExplorerFile(document.Metadata.Path, document.Metadata.Status, document.Metadata.Language)));

    public static ImmutableArray<FileExplorerNode> Build(IEnumerable<FileExplorerFile> files)
    {
        var root = new MutableFolder(string.Empty, string.Empty);
        foreach (var file in files)
        {
            var normalizedPath = NormalizePath(file.Path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var current = root;
            for (var segmentIndex = 0; segmentIndex < segments.Length - 1; segmentIndex++)
            {
                var segment = segments[segmentIndex];
                var folderPath = JoinPath(current.Path, segment);
                if (!current.Folders.TryGetValue(segment, out var folder))
                {
                    folder = new MutableFolder(segment, folderPath);
                    current.Folders.Add(segment, folder);
                }

                current = folder;
            }

            var fileName = segments[^1];
            current.Files[fileName] = new FileExplorerFile(normalizedPath, file.Status, string.IsNullOrWhiteSpace(file.Language) ? "Text" : file.Language);
        }

        return root.ToChildren();
    }

    public static FileExplorerIconKind GetIconKind(string path, string language)
    {
        var fileName = System.IO.Path.GetFileName(path).ToLowerInvariant();
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var normalizedLanguage = language.Trim().ToLowerInvariant();

        if (fileName is ".gitignore" or ".gitattributes" or ".gitmodules")
        {
            return FileExplorerIconKind.Git;
        }

        if (extension is ".sln" or ".slnx")
        {
            return FileExplorerIconKind.Solution;
        }

        if (extension is ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets")
        {
            return FileExplorerIconKind.Project;
        }

        if (extension == ".cs" || normalizedLanguage is "c#" or "csharp")
        {
            return FileExplorerIconKind.CSharp;
        }

        if (extension is ".xaml" or ".axaml" || normalizedLanguage.Contains("xaml", StringComparison.Ordinal))
        {
            return FileExplorerIconKind.Xaml;
        }

        if (extension == ".xml" || normalizedLanguage == "xml")
        {
            return FileExplorerIconKind.Xml;
        }

        if (extension == ".json" || normalizedLanguage == "json")
        {
            return FileExplorerIconKind.Json;
        }

        if (extension is ".md" or ".markdown")
        {
            return FileExplorerIconKind.Markdown;
        }

        if (extension is ".yml" or ".yaml" or ".toml" or ".editorconfig" or ".config" or ".ini")
        {
            return FileExplorerIconKind.Config;
        }

        if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".ico")
        {
            return FileExplorerIconKind.Image;
        }

        if (extension is ".txt" or ".log")
        {
            return FileExplorerIconKind.Text;
        }

        return string.IsNullOrWhiteSpace(extension) ? FileExplorerIconKind.Binary : FileExplorerIconKind.File;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    private static string JoinPath(string prefix, string name) => string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix}/{name}";

    private static DiffFileStatus AggregateStatus(IEnumerable<DiffFileStatus> statuses)
    {
        var result = DiffFileStatus.Unchanged;
        var resultPriority = -1;
        foreach (var status in statuses)
        {
            var priority = GetStatusPriority(status);
            if (priority > resultPriority)
            {
                result = status;
                resultPriority = priority;
            }
        }

        return result;
    }

    private static int GetStatusPriority(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Conflicted => 7,
        DiffFileStatus.Deleted => 6,
        DiffFileStatus.Renamed => 5,
        DiffFileStatus.Copied => 5,
        DiffFileStatus.Added => 4,
        DiffFileStatus.Untracked => 4,
        DiffFileStatus.Modified => 3,
        _ => 0
    };

    private sealed class MutableFolder
    {
        public MutableFolder(string name, string path)
        {
            Name = name;
            Path = path;
        }

        public string Name { get; }

        public string Path { get; }

        public SortedDictionary<string, MutableFolder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SortedDictionary<string, FileExplorerFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ImmutableArray<FileExplorerNode> ToChildren()
        {
            var builder = ImmutableArray.CreateBuilder<FileExplorerNode>(Folders.Count + Files.Count);
            foreach (var folder in Folders.Values)
            {
                var children = folder.ToChildren();
                builder.Add(new FileExplorerNode(
                    folder.Name,
                    folder.Path,
                    FileExplorerNodeKind.Folder,
                    AggregateStatus(children.Select(child => child.Status)),
                    "Folder",
                    FileExplorerIconKind.Folder,
                    null,
                    children));
            }

            foreach (var file in Files.Values)
            {
                var fileName = System.IO.Path.GetFileName(file.Path);
                builder.Add(new FileExplorerNode(
                    fileName,
                    file.Path,
                    FileExplorerNodeKind.File,
                    file.Status,
                    file.Language,
                    GetIconKind(file.Path, file.Language),
                    file.Path,
                    ImmutableArray<FileExplorerNode>.Empty));
            }

            return builder.ToImmutable();
        }
    }
}