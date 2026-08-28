using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PyQsirchgui.Windows.Services;

public static class ShellPreviewService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "3gp", "3g2", "asf", "avi", "flv", "m2ts", "m4v", "mkv", "mov", "mp4", "mpeg", "mpg", "mts", "ts", "webm", "wmv",
    };

    public static bool IsVideoFile(string extension)
    {
        return VideoExtensions.Contains(extension.Trim().TrimStart('.'));
    }

    public static byte[]? RenderPreview(string path, int size = 1600)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return RenderShellImage(path, size, ShellImageFlags.BiggerSizeOk);
    }

    public static byte[]? RenderIcon(string path, int size = 96)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        return RenderShellImage(path, size, ShellImageFlags.BiggerSizeOk | ShellImageFlags.IconOnly);
    }

    public static ImageSource? FileTypeIcon(string extension, bool isFolder, bool large = true)
    {
        var flags = ShellFileInfoFlags.Icon |
                    ShellFileInfoFlags.UseFileAttributes |
                    (large ? ShellFileInfoFlags.LargeIcon : ShellFileInfoFlags.SmallIcon);
        var attributes = isFolder ? FileAttributes.Directory : FileAttributes.Normal;
        var name = isFolder ? "folder" : string.IsNullOrWhiteSpace(extension) ? "file" : "." + extension.TrimStart('.');
        var info = new ShellFileInfo();
        var result = SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.IconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private static byte[]? RenderShellImage(string path, int size, ShellImageFlags flags)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            factory.GetImage(new NativeSize { Width = size, Height = size }, flags, out bitmap);
            if (bitmap == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        FileAttributes fileAttributes,
        ref ShellFileInfo shellFileInfo,
        uint fileInfoSize,
        ShellFileInfoFlags flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, ShellImageFlags flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [Flags]
    private enum ShellImageFlags
    {
        ResizeToFit = 0,
        BiggerSizeOk = 1,
        IconOnly = 4,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [Flags]
    private enum ShellFileInfoFlags : uint
    {
        Icon = 0x000000100,
        LargeIcon = 0x000000000,
        SmallIcon = 0x000000001,
        UseFileAttributes = 0x000000010,
    }
}
