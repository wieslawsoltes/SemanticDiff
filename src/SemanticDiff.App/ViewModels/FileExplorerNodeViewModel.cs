using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SemanticDiff.App.Services;
using SemanticDiff.Core;
using Windows.UI;

namespace SemanticDiff.App.ViewModels;

public sealed record FileExplorerNodeViewModel(
    string Name,
    string Path,
    string? DocumentId,
    FileExplorerNodeKind Kind,
    DiffFileStatus Status,
    string Language,
    FileExplorerIconKind IconKind,
    string? RepositoryRoot,
    int Depth,
    bool IsExpanded,
    bool HasChildren,
    int ChildCount,
    bool IsLightTheme,
    bool UseImageIcons)
{
    private Lazy<SystemFileIconDescriptor> IconDescriptor { get; } =
        new(() => SystemFileIconProvider.Current.GetIcon(RepositoryRoot, Path, Kind, IconKind));

    public bool IsFile => Kind == FileExplorerNodeKind.File;

    public bool CanDragToEditorCanvas => !string.IsNullOrWhiteSpace(Path);

    public bool CanNavigateToNode => IsFile && !string.IsNullOrWhiteSpace(DocumentId);

    public string DisplayPath => IsFile ? Path : FormatFolderPath(Path, ChildCount);

    public string StatusText => IsFile ? FileStatusText(Status) : ChildCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string DisclosureGlyph => HasChildren ? IsExpanded ? "\uE70D" : "\uE76C" : string.Empty;

    public Visibility DisclosureVisibility { get; } = HasChildren ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? IconSource => UseImageIcons ? IconDescriptor.Value.Source : null;

    public Visibility IconSourceVisibility => IconSource is null ? Visibility.Collapsed : Visibility.Visible;

    public string FallbackIconGlyph => UseImageIcons ? IconDescriptor.Value.FallbackGlyph : GetFastFallbackGlyph(IconKind);

    public FontFamily FallbackIconFontFamily { get; } = SystemFileIconProvider.Current.FallbackFontFamily;

    public Visibility FallbackIconVisibility => IconSource is null ? Visibility.Visible : Visibility.Collapsed;

    public string IconAutomationText => UseImageIcons ? IconDescriptor.Value.AutomationText : GetFastAutomationText(IconKind);

    public string PlatformIconText { get; } = SystemFileIconProvider.Current.PlatformText;

    public Thickness IndentMargin { get; } = new(Math.Min(48, Depth * 14), 0, 0, 0);

    public SolidColorBrush StatusBrush { get; } = DiffStatusBrushes.GetBrush(Status, IsLightTheme);

    public SolidColorBrush StatusSoftBrush { get; } = DiffStatusBrushes.GetSoftBrush(Status, IsLightTheme);

    public string ContextMenuPrimaryText => IsFile ? "Navigate to node" : IsExpanded ? "Collapse folder" : "Expand folder";

    public string SearchText => $"{Name} {Path} {Language} {Status} {IconKind}";

    public static ImmutableArray<FileExplorerNodeViewModel> Flatten(
        ImmutableArray<FileExplorerNode> roots,
        ImmutableHashSet<string> collapsedPaths,
        bool forceExpanded,
        string? repositoryRoot,
        bool isLightTheme,
        bool useImageIcons)
    {
        var builder = ImmutableArray.CreateBuilder<FileExplorerNodeViewModel>();
        foreach (var root in roots)
        {
            AddVisibleNode(root, depth: 0, collapsedPaths, forceExpanded, repositoryRoot, isLightTheme, useImageIcons, builder);
        }

        return builder.ToImmutable();
    }

    private static void AddVisibleNode(
        FileExplorerNode node,
        int depth,
        ImmutableHashSet<string> collapsedPaths,
        bool forceExpanded,
        string? repositoryRoot,
        bool isLightTheme,
        bool useImageIcons,
        ImmutableArray<FileExplorerNodeViewModel>.Builder builder)
    {
        var isExpanded = node.Kind == FileExplorerNodeKind.Folder && (forceExpanded || !collapsedPaths.Contains(node.Path));
        builder.Add(new FileExplorerNodeViewModel(
            node.Name,
            node.Path,
            node.DocumentId,
            node.Kind,
            node.Status,
            node.Language,
            node.IconKind,
            repositoryRoot,
            depth,
            isExpanded,
            node.HasChildren,
            node.Children.Length,
            isLightTheme,
            useImageIcons));

        if (!isExpanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            AddVisibleNode(child, depth + 1, collapsedPaths, forceExpanded, repositoryRoot, isLightTheme, useImageIcons, builder);
        }
    }

    private static string FormatFolderPath(string path, int childCount) => string.IsNullOrWhiteSpace(path)
        ? $"/{childCount:N0}"
        : $"{path}/";

    private static string FileStatusText(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Added => "A",
        DiffFileStatus.Deleted => "D",
        DiffFileStatus.Renamed => "R",
        DiffFileStatus.Copied => "C",
        DiffFileStatus.Untracked => "?",
        DiffFileStatus.Conflicted => "!",
        DiffFileStatus.Unchanged => "=",
        _ => "M"
    };

    private static string GetFastFallbackGlyph(FileExplorerIconKind iconKind) => iconKind switch
    {
        FileExplorerIconKind.Folder => "\uE8B7",
        FileExplorerIconKind.CSharp or FileExplorerIconKind.Xaml or FileExplorerIconKind.Xml or FileExplorerIconKind.Json => "\uE943",
        FileExplorerIconKind.Project or FileExplorerIconKind.Solution => "\uE8F1",
        FileExplorerIconKind.Config => "\uE713",
        FileExplorerIconKind.Git => "\uE8EE",
        FileExplorerIconKind.Image => "\uEB9F",
        _ => "\uE8A5"
    };

    private static string GetFastAutomationText(FileExplorerIconKind iconKind) => iconKind switch
    {
        FileExplorerIconKind.Folder => "Folder",
        FileExplorerIconKind.CSharp => "C# file",
        FileExplorerIconKind.Xaml => "XAML file",
        FileExplorerIconKind.Xml => "XML file",
        FileExplorerIconKind.Json => "JSON file",
        FileExplorerIconKind.Markdown => "Markdown file",
        FileExplorerIconKind.Project => "Project file",
        FileExplorerIconKind.Solution => "Solution file",
        FileExplorerIconKind.Config => "Configuration file",
        FileExplorerIconKind.Git => "Git file",
        FileExplorerIconKind.Image => "Image file",
        FileExplorerIconKind.Text => "Text file",
        FileExplorerIconKind.Binary => "Binary file",
        _ => "File"
    };
}

internal static class DiffStatusBrushes
{
    private static readonly Dictionary<(DiffFileStatus Status, bool IsLightTheme, bool IsSoft), SolidColorBrush> Cache = [];

    public static SolidColorBrush GetBrush(DiffFileStatus status, bool isLightTheme) =>
        GetCachedBrush(status, isLightTheme, isSoft: false);

    public static SolidColorBrush GetSoftBrush(DiffFileStatus status, bool isLightTheme) =>
        GetCachedBrush(status, isLightTheme, isSoft: true);

    private static SolidColorBrush GetCachedBrush(DiffFileStatus status, bool isLightTheme, bool isSoft)
    {
        var key = (status, isLightTheme, isSoft);
        if (Cache.TryGetValue(key, out var brush))
        {
            return brush;
        }

        var color = GetStatusColor(status, isLightTheme);
        brush = isSoft
            ? new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B))
            : new SolidColorBrush(color);
        Cache[key] = brush;
        return brush;
    }

    private static Color GetStatusColor(DiffFileStatus status, bool isLightTheme) => status switch
    {
        DiffFileStatus.Added => isLightTheme ? Color.FromArgb(255, 26, 127, 55) : Color.FromArgb(255, 46, 160, 67),
        DiffFileStatus.Untracked => isLightTheme ? Color.FromArgb(255, 26, 127, 55) : Color.FromArgb(255, 63, 185, 80),
        DiffFileStatus.Deleted => isLightTheme ? Color.FromArgb(255, 207, 34, 46) : Color.FromArgb(255, 248, 81, 73),
        DiffFileStatus.Renamed or DiffFileStatus.Copied => isLightTheme ? Color.FromArgb(255, 130, 80, 223) : Color.FromArgb(255, 163, 113, 247),
        DiffFileStatus.Conflicted => isLightTheme ? Color.FromArgb(255, 188, 76, 0) : Color.FromArgb(255, 249, 115, 22),
        DiffFileStatus.Unchanged => isLightTheme ? Color.FromArgb(255, 82, 97, 114) : Color.FromArgb(255, 153, 166, 182),
        _ => isLightTheme ? Color.FromArgb(255, 154, 103, 0) : Color.FromArgb(255, 210, 153, 34)
    };
}
