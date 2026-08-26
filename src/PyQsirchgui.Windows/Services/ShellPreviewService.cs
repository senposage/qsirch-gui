using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PyQsirchgui.Windows.Services;

public static class ShellPreviewService
{
    public static byte[]? RenderPreview(string path, int size = 1600)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            factory.GetImage(new NativeSize { Width = size, Height = size }, ShellImageFlags.BiggerSizeOk, out bitmap);
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
    }
}
