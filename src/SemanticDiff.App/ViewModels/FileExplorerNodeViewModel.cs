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
    bool IsLightTheme)
{
    public bool IsFile => Kind == FileExplorerNodeKind.File;

    public bool CanDragToEditorCanvas => !string.IsNullOrWhiteSpace(Path);

    public bool CanNavigateToNode => IsFile && !string.IsNullOrWhiteSpace(DocumentId);

    public string DisplayPath => IsFile ? Path : FormatFolderPath(Path, ChildCount);

    public string StatusText => IsFile ? FileStatusText(Status) : ChildCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string DisclosureGlyph => HasChildren ? IsExpanded ? "\uE70D" : "\uE76C" : string.Empty;

    public Visibility DisclosureVisibility => HasChildren ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? IconSource => SystemFileIconProvider.Current.GetIcon(RepositoryRoot, Path, Kind, IconKind).Source;

    public Visibility IconSourceVisibility => IconSource is null ? Visibility.Collapsed : Visibility.Visible;

    public string FallbackIconGlyph => SystemFileIconProvider.Current.GetIcon(RepositoryRoot, Path, Kind, IconKind).FallbackGlyph;

    public FontFamily FallbackIconFontFamily => SystemFileIconProvider.Current.FallbackFontFamily;

    public Visibility FallbackIconVisibility => IconSource is null ? Visibility.Visible : Visibility.Collapsed;

    public string IconAutomationText => SystemFileIconProvider.Current.GetIcon(RepositoryRoot, Path, Kind, IconKind).AutomationText;

    public string PlatformIconText => SystemFileIconProvider.Current.PlatformText;

    public Thickness IndentMargin => new(Math.Min(48, Depth * 14), 0, 0, 0);

    public SolidColorBrush StatusBrush => DiffStatusBrushes.GetBrush(Status, IsLightTheme);

    public SolidColorBrush StatusSoftBrush => DiffStatusBrushes.GetSoftBrush(Status, IsLightTheme);

    public string ContextMenuPrimaryText => IsFile ? "Navigate to node" : IsExpanded ? "Collapse folder" : "Expand folder";

    public string SearchText => $"{Name} {Path} {Language} {Status} {IconKind}";

    public static ImmutableArray<FileExplorerNodeViewModel> Flatten(
        ImmutableArray<FileExplorerNode> roots,
        ImmutableHashSet<string> collapsedPaths,
        bool forceExpanded,
        string? repositoryRoot,
        bool isLightTheme)
    {
        var builder = ImmutableArray.CreateBuilder<FileExplorerNodeViewModel>();
        foreach (var root in roots)
        {
            AddVisibleNode(root, depth: 0, collapsedPaths, forceExpanded, repositoryRoot, isLightTheme, builder);
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
            isLightTheme));

        if (!isExpanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            AddVisibleNode(child, depth + 1, collapsedPaths, forceExpanded, repositoryRoot, isLightTheme, builder);
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
}

internal static class DiffStatusBrushes
{
    public static SolidColorBrush GetBrush(DiffFileStatus status, bool isLightTheme) => new(GetStatusColor(status, isLightTheme));

    public static SolidColorBrush GetSoftBrush(DiffFileStatus status, bool isLightTheme)
    {
        var color = GetStatusColor(status, isLightTheme);
        return new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B));
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
