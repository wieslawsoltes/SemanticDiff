using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SemanticDiff.Core;

namespace SemanticDiff.App.Services;

internal sealed record SystemFileIconDescriptor(
    ImageSource? Source,
    string FallbackGlyph,
    string AutomationText,
    string CacheKey);

internal sealed record SystemFileIconRequest(
    string RelativePath,
    string? AbsolutePath,
    FileExplorerNodeKind NodeKind,
    FileExplorerIconKind IconKind,
    string CacheKey,
    string LookupValue,
    bool UseExistingPath);

internal abstract class SystemFileIconProvider
{
    private static readonly Lazy<SystemFileIconProvider> LazyCurrent = new(CreateCurrent, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ConcurrentDictionary<string, Lazy<SystemFileIconDescriptor>> iconCache = new(StringComparer.OrdinalIgnoreCase);

    public static SystemFileIconProvider Current => LazyCurrent.Value;

    public FontFamily FallbackFontFamily { get; } = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public abstract string PlatformText { get; }

    public SystemFileIconDescriptor GetIcon(
        string? repositoryRoot,
        string relativePath,
        FileExplorerNodeKind nodeKind,
        FileExplorerIconKind iconKind)
    {
        var request = CreateRequest(repositoryRoot, relativePath, nodeKind, iconKind);
        var cached = iconCache.GetOrAdd(
            request.CacheKey,
            _ => new Lazy<SystemFileIconDescriptor>(() => LoadIcon(request), LazyThreadSafetyMode.ExecutionAndPublication));

        return cached.Value;
    }

    protected abstract byte[]? TryGetSystemIconPng(SystemFileIconRequest request);

    private SystemFileIconDescriptor LoadIcon(SystemFileIconRequest request)
    {
        var source = TryCreateImageSource(request);
        return new SystemFileIconDescriptor(
            source,
            GetFallbackGlyph(request.IconKind),
            GetAutomationText(request.IconKind),
            request.CacheKey);
    }

    private ImageSource? TryCreateImageSource(SystemFileIconRequest request)
    {
        try
        {
            var bytes = TryGetSystemIconPng(request);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            var iconPath = GetCachedIconPath(request.CacheKey);
            if (!File.Exists(iconPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                File.WriteAllBytes(iconPath, bytes);
            }

            return new BitmapImage(new Uri(iconPath, UriKind.Absolute));
        }
        catch
        {
            return null;
        }
    }

    private static SystemFileIconRequest CreateRequest(
        string? repositoryRoot,
        string relativePath,
        FileExplorerNodeKind nodeKind,
        FileExplorerIconKind iconKind)
    {
        var normalizedPath = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        var absolutePath = ResolveAbsolutePath(repositoryRoot, normalizedPath);
        var useExistingPath = !string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath);

        if (nodeKind == FileExplorerNodeKind.Folder)
        {
            useExistingPath = !string.IsNullOrWhiteSpace(absolutePath) && Directory.Exists(absolutePath);
            return new SystemFileIconRequest(
                normalizedPath,
                absolutePath,
                nodeKind,
                iconKind,
                "folder",
                useExistingPath ? absolutePath! : string.Empty,
                useExistingPath);
        }

        var fileName = Path.GetFileName(normalizedPath);
        var extension = Path.GetExtension(normalizedPath).TrimStart('.');
        var lookupValue = useExistingPath ? absolutePath! : extension;
        var cacheKey = !string.IsNullOrWhiteSpace(extension)
            ? $"extension:{extension.ToLowerInvariant()}"
            : $"name:{fileName.ToLowerInvariant()}";

        return new SystemFileIconRequest(
            normalizedPath,
            absolutePath,
            nodeKind,
            iconKind,
            cacheKey,
            lookupValue,
            useExistingPath);
    }

    private static string? ResolveAbsolutePath(string? repositoryRoot, string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(repositoryRoot, normalizedPath));
        }
        catch
        {
            return null;
        }
    }

    private static string GetCachedIconPath(string cacheKey)
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SemanticDiff",
            "SystemFileIcons",
            GetPlatformCacheName());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();
        return Path.Combine(cacheRoot, $"{hash}.png");
    }

    private static string GetPlatformCacheName()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        return "generic";
    }

    private static string GetFallbackGlyph(FileExplorerIconKind iconKind) => iconKind switch
    {
        FileExplorerIconKind.Folder => "\uE8B7",
        FileExplorerIconKind.CSharp or FileExplorerIconKind.Xaml or FileExplorerIconKind.Xml or FileExplorerIconKind.Json => "\uE943",
        FileExplorerIconKind.Project or FileExplorerIconKind.Solution => "\uE8F1",
        FileExplorerIconKind.Config => "\uE713",
        FileExplorerIconKind.Git => "\uE8EE",
        FileExplorerIconKind.Image => "\uEB9F",
        _ => "\uE8A5"
    };

    private static string GetAutomationText(FileExplorerIconKind iconKind) => iconKind switch
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

    private static SystemFileIconProvider CreateCurrent()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacOSSystemFileIconProvider();
        }

        return new FallbackSystemFileIconProvider();
    }
}

internal sealed class FallbackSystemFileIconProvider : SystemFileIconProvider
{
    public override string PlatformText => "Fallback icon profile";

    protected override byte[]? TryGetSystemIconPng(SystemFileIconRequest request) => null;
}

internal sealed class MacOSSystemFileIconProvider : SystemFileIconProvider
{
    public override string PlatformText => "macOS NSWorkspace system icons, extension cached";

    protected override byte[]? TryGetSystemIconPng(SystemFileIconRequest request) => MacOSFileIconInterop.TryGetIconPng(request);
}

internal static class MacOSFileIconInterop
{
    private const string AppKitLibrary = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string FoundationLibrary = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const ulong PngFileType = 4;

    private static readonly IntPtr AllocSelector = sel_registerName("alloc");
    private static readonly IntPtr InitSelector = sel_registerName("init");
    private static readonly IntPtr DrainSelector = sel_registerName("drain");
    private static readonly IntPtr SharedWorkspaceSelector = sel_registerName("sharedWorkspace");
    private static readonly IntPtr IconForFileSelector = sel_registerName("iconForFile:");
    private static readonly IntPtr IconForFileTypeSelector = sel_registerName("iconForFileType:");
    private static readonly IntPtr InitWithUtf8StringSelector = sel_registerName("initWithUTF8String:");
    private static readonly IntPtr TiffRepresentationSelector = sel_registerName("TIFFRepresentation");
    private static readonly IntPtr InitWithDataSelector = sel_registerName("initWithData:");
    private static readonly IntPtr RepresentationUsingTypePropertiesSelector = sel_registerName("representationUsingType:properties:");
    private static readonly IntPtr LengthSelector = sel_registerName("length");
    private static readonly IntPtr BytesSelector = sel_registerName("bytes");
    private static readonly IntPtr ReleaseSelector = sel_registerName("release");

    static MacOSFileIconInterop()
    {
        NativeLibrary.Load(AppKitLibrary);
        NativeLibrary.Load(FoundationLibrary);
    }

    public static byte[]? TryGetIconPng(SystemFileIconRequest request)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var pool = IntPtr.Zero;
        var lookupString = IntPtr.Zero;
        var bitmapRepresentation = IntPtr.Zero;
        try
        {
            pool = CreateAutoreleasePool();
            var workspace = IntPtr_objc_msgSend(objc_getClass("NSWorkspace"), SharedWorkspaceSelector);
            if (workspace == IntPtr.Zero)
            {
                return null;
            }

            var icon = request.UseExistingPath && !string.IsNullOrWhiteSpace(request.LookupValue)
                ? GetIconForFile(workspace, request.LookupValue, ref lookupString)
                : GetIconForFileType(workspace, request.LookupValue, request.NodeKind, ref lookupString);

            if (icon == IntPtr.Zero)
            {
                return null;
            }

            var tiffData = IntPtr_objc_msgSend(icon, TiffRepresentationSelector);
            if (tiffData == IntPtr.Zero)
            {
                return null;
            }

            bitmapRepresentation = IntPtr_objc_msgSend_IntPtr(
                IntPtr_objc_msgSend(objc_getClass("NSBitmapImageRep"), AllocSelector),
                InitWithDataSelector,
                tiffData);
            if (bitmapRepresentation == IntPtr.Zero)
            {
                return null;
            }

            var pngData = IntPtr_objc_msgSend_UIntPtr_IntPtr(
                bitmapRepresentation,
                RepresentationUsingTypePropertiesSelector,
                (UIntPtr)PngFileType,
                IntPtr.Zero);
            if (pngData == IntPtr.Zero)
            {
                return null;
            }

            var length = UIntPtr_objc_msgSend(pngData, LengthSelector);
            var bytes = IntPtr_objc_msgSend(pngData, BytesSelector);
            if (length == UIntPtr.Zero || bytes == IntPtr.Zero || length.ToUInt64() > int.MaxValue)
            {
                return null;
            }

            var managedBytes = new byte[(int)length.ToUInt64()];
            Marshal.Copy(bytes, managedBytes, 0, managedBytes.Length);
            return managedBytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmapRepresentation != IntPtr.Zero)
            {
                void_objc_msgSend(bitmapRepresentation, ReleaseSelector);
            }

            if (lookupString != IntPtr.Zero)
            {
                void_objc_msgSend(lookupString, ReleaseSelector);
            }

            if (pool != IntPtr.Zero)
            {
                void_objc_msgSend(pool, DrainSelector);
            }
        }
    }

    private static IntPtr GetIconForFile(IntPtr workspace, string path, ref IntPtr lookupString)
    {
        lookupString = CreateNSString(path);
        return lookupString == IntPtr.Zero ? IntPtr.Zero : IntPtr_objc_msgSend_IntPtr(workspace, IconForFileSelector, lookupString);
    }

    private static IntPtr GetIconForFileType(
        IntPtr workspace,
        string extension,
        FileExplorerNodeKind nodeKind,
        ref IntPtr lookupString)
    {
        var lookupValue = nodeKind == FileExplorerNodeKind.Folder
            ? "public.folder"
            : string.IsNullOrWhiteSpace(extension)
                ? string.Empty
                : extension.TrimStart('.');
        lookupString = CreateNSString(lookupValue);
        return lookupString == IntPtr.Zero ? IntPtr.Zero : IntPtr_objc_msgSend_IntPtr(workspace, IconForFileTypeSelector, lookupString);
    }

    private static IntPtr CreateAutoreleasePool()
    {
        var poolClass = objc_getClass("NSAutoreleasePool");
        return IntPtr_objc_msgSend(IntPtr_objc_msgSend(poolClass, AllocSelector), InitSelector);
    }

    private static IntPtr CreateNSString(string value)
    {
        var stringClass = objc_getClass("NSString");
        var allocated = IntPtr_objc_msgSend(stringClass, AllocSelector);
        return IntPtr_objc_msgSend_String(allocated, InitWithUtf8StringSelector, value);
    }

    [DllImport(ObjCLibrary)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLibrary)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend_String(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend_UIntPtr_IntPtr(IntPtr receiver, IntPtr selector, UIntPtr value, IntPtr dictionary);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern UIntPtr UIntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend(IntPtr receiver, IntPtr selector);
}