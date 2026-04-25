using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
    int Depth,
    bool IsExpanded,
    bool HasChildren,
    int ChildCount,
    bool IsLightTheme)
{
    public bool IsFile => Kind == FileExplorerNodeKind.File;

    public bool CanNavigateToNode => IsFile && !string.IsNullOrWhiteSpace(DocumentId);

    public string DisplayPath => IsFile ? Path : FormatFolderPath(Path, ChildCount);

    public string StatusText => IsFile ? FileStatusText(Status) : ChildCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string DisclosureGlyph => HasChildren ? IsExpanded ? "\uE70D" : "\uE76C" : string.Empty;

    public string IconGlyph => NativeFileIconProvider.GetIcon(IconKind).Glyph;

    public FontFamily IconFontFamily => NativeFileIconProvider.Current.FontFamily;

    public string IconAutomationText => NativeFileIconProvider.GetIcon(IconKind).AutomationText;

    public string PlatformIconText => NativeFileIconProvider.Current.PlatformText;

    public Thickness IndentMargin => new(Math.Min(48, Depth * 14), 0, 0, 0);

    public SolidColorBrush StatusBrush => DiffStatusBrushes.GetBrush(Status, IsLightTheme);

    public SolidColorBrush StatusSoftBrush => DiffStatusBrushes.GetSoftBrush(Status, IsLightTheme);

    public string ContextMenuPrimaryText => IsFile ? "Navigate to node" : IsExpanded ? "Collapse folder" : "Expand folder";

    public string SearchText => $"{Name} {Path} {Language} {Status} {IconKind}";

    public static ImmutableArray<FileExplorerNodeViewModel> Flatten(
        ImmutableArray<FileExplorerNode> roots,
        ImmutableHashSet<string> collapsedPaths,
        bool forceExpanded,
        bool isLightTheme)
    {
        var builder = ImmutableArray.CreateBuilder<FileExplorerNodeViewModel>();
        foreach (var root in roots)
        {
            AddVisibleNode(root, depth: 0, collapsedPaths, forceExpanded, isLightTheme, builder);
        }

        return builder.ToImmutable();
    }

    private static void AddVisibleNode(
        FileExplorerNode node,
        int depth,
        ImmutableHashSet<string> collapsedPaths,
        bool forceExpanded,
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
            AddVisibleNode(child, depth + 1, collapsedPaths, forceExpanded, isLightTheme, builder);
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

internal sealed record NativeFileIconProfile(FontFamily FontFamily, string PlatformText)
{
    public string FolderGlyph { get; init; } = "\uE8B7";

    public string FileGlyph { get; init; } = "\uE8A5";

    public string CodeGlyph { get; init; } = "\uE943";

    public string ProjectGlyph { get; init; } = "\uE8F1";

    public string SolutionGlyph { get; init; } = "\uE8F1";

    public string ConfigGlyph { get; init; } = "\uE713";

    public string GitGlyph { get; init; } = "\uE8EE";

    public string ImageGlyph { get; init; } = "\uEB9F";

    public string TextGlyph { get; init; } = "\uE8A5";

}

internal sealed record NativeFileIconDescriptor(string Glyph, string AutomationText);

internal static class NativeFileIconProvider
{
    public static NativeFileIconProfile Current { get; } = CreateProfile();

    public static NativeFileIconDescriptor GetIcon(FileExplorerIconKind iconKind)
    {
        var profile = Current;
        return iconKind switch
        {
            FileExplorerIconKind.Folder => new(profile.FolderGlyph, "Folder"),
            FileExplorerIconKind.CSharp => new(profile.CodeGlyph, "C# file"),
            FileExplorerIconKind.Xaml => new(profile.CodeGlyph, "XAML file"),
            FileExplorerIconKind.Xml => new(profile.CodeGlyph, "XML file"),
            FileExplorerIconKind.Json => new(profile.CodeGlyph, "JSON file"),
            FileExplorerIconKind.Markdown => new(profile.TextGlyph, "Markdown file"),
            FileExplorerIconKind.Project => new(profile.ProjectGlyph, "Project file"),
            FileExplorerIconKind.Solution => new(profile.SolutionGlyph, "Solution file"),
            FileExplorerIconKind.Config => new(profile.ConfigGlyph, "Configuration file"),
            FileExplorerIconKind.Git => new(profile.GitGlyph, "Git file"),
            FileExplorerIconKind.Image => new(profile.ImageGlyph, "Image file"),
            FileExplorerIconKind.Text => new(profile.TextGlyph, "Text file"),
            _ => new(profile.FileGlyph, "File")
        };
    }

    private static NativeFileIconProfile CreateProfile()
    {
        if (OperatingSystem.IsWindows())
        {
            return new NativeFileIconProfile(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), "Windows native icon profile");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new NativeFileIconProfile(new FontFamily("Apple Symbols, SF Pro, Segoe Fluent Icons"), "macOS native icon profile");
        }

        if (OperatingSystem.IsLinux())
        {
            return new NativeFileIconProfile(new FontFamily("Symbols Nerd Font, Noto Sans Symbols 2, Segoe Fluent Icons"), "Linux native icon profile");
        }

        return new NativeFileIconProfile(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), "Cross-platform icon profile");
    }
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