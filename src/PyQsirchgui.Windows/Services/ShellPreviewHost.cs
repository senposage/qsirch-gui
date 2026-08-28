using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;

namespace PyQsirchgui.Windows.Services;

public sealed class ShellPreviewHost : HwndHost
{
    private const string PreviewHandlerCategory = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const string PreviewHandlerInterfaceId = "8895b1c6-b41f-4c1c-a562-0d564250836f";
    private const uint StorageRead = 0;
    private const uint StorageShareDenyNone = 0x40;
    private const uint ClsctxLocalServer = 0x4;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    private readonly string _path;
    private readonly Guid _handlerClassId;
    private IPreviewHandler? _handler;
    private IStream? _stream;
    private IShellItem? _shellItem;
    private PreviewHandlerFrame? _site;
    private IntPtr _childHandle;
    private bool _previewStarted;

    public event EventHandler<PreviewFailureEventArgs>? PreviewFailed;

    private ShellPreviewHost(string path, Guid handlerClassId)
    {
        _path = path;
        _handlerClassId = handlerClassId;
    }

    public static ShellPreviewHost? TryCreate(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(name) || name.Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Warn("preview", $"native preview skipped invalid file name path=\"{path}\"");
            return null;
        }

        if (!File.Exists(path))
        {
            AppLogger.Warn("preview", $"native preview skipped file not found path=\"{path}\"");
            return null;
        }

        if (!TryFindPreviewHandler(Path.GetExtension(path), out var handlerClassId))
        {
            return null;
        }

        return new ShellPreviewHost(path, handlerClassId);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _childHandle = CreateWindowEx(
            0,
            "static",
            "",
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            0,
            0,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_childHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows could not create a preview host.");
        }

        try
        {
            _handler = CreatePreviewHandler(_handlerClassId);
            var initializationMode = "";
            if (_handler is IObjectWithSite objectWithSite)
            {
                _site = new PreviewHandlerFrame(_childHandle);
                objectWithSite.SetSite(_site);
                AppLogger.Info("preview", $"native handler site attached path=\"{_path}\"");
            }
            else
            {
                AppLogger.Info("preview", $"native handler does not expose IObjectWithSite path=\"{_path}\"");
            }
            if (_handler is IInitializeWithStream initializeWithStream)
            {
                _stream = OpenReadStream(_path);
                initializeWithStream.Initialize(_stream, StorageRead);
                initializationMode = "stream";
            }
            else if (_handler is IInitializeWithFile initializeWithFile)
            {
                initializeWithFile.Initialize(_path, StorageRead);
                initializationMode = "file";
            }
            else if (_handler is IInitializeWithItem initializeWithItem)
            {
                _shellItem = CreateShellItem(_path);
                initializeWithItem.Initialize(_shellItem, StorageRead);
                initializationMode = "shell-item";
            }
            else
            {
                throw new InvalidOperationException("The registered preview handler does not support file, shell-item, or stream initialization.");
            }
            AppLogger.Info("preview", $"native handler initialized mode={initializationMode} path=\"{_path}\" handler=\"{_handlerClassId}\"");
        }
        catch (Exception ex)
        {
            FailPreview(ex, "initialize");
        }

        return new HandleRef(this, _childHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        ReleaseHandler();
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }
        _childHandle = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_handler == null)
        {
            return;
        }

        try
        {
            var rect = new NativeRect(0, 0, Math.Max(0, (int)rcBoundingBox.Width), Math.Max(0, (int)rcBoundingBox.Height));
            if (!_previewStarted)
            {
                AppLogger.Info("preview", $"native handler starting path=\"{_path}\" size={rect.Right}x{rect.Bottom}");
                _handler.SetWindow(_childHandle, ref rect);
                _handler.SetRect(ref rect);
                _handler.DoPreview();
                _previewStarted = true;
                AppLogger.Info("preview", $"native handler started path=\"{_path}\"");
            }
            else
            {
                _handler.SetRect(ref rect);
            }
        }
        catch (Exception ex)
        {
            FailPreview(ex, "start or resize");
        }
    }

    private void FailPreview(Exception ex, string operation)
    {
        AppLogger.Error("preview", ex, $"native preview {operation} failed path=\"{_path}\" handler=\"{_handlerClassId}\"");
        ReleaseHandler();
        Dispatcher.BeginInvoke(() => PreviewFailed?.Invoke(this, new PreviewFailureEventArgs("Windows could not load the registered preview handler for this file.")));
    }

    private void ReleaseHandler()
    {
        if (_handler == null)
        {
            return;
        }

        try
        {
            if (_handler is IObjectWithSite objectWithSite)
            {
                objectWithSite.SetSite(null);
            }
            _handler.Unload();
        }
        catch
        {
        }
        finally
        {
            Marshal.FinalReleaseComObject(_handler);
            _handler = null;
            _site = null;
            if (_stream != null)
            {
                Marshal.FinalReleaseComObject(_stream);
                _stream = null;
            }
            if (_shellItem != null)
            {
                Marshal.FinalReleaseComObject(_shellItem);
                _shellItem = null;
            }
        }
    }

    private static IStream OpenReadStream(string path)
    {
        SHCreateStreamOnFileEx(path, StorageRead | StorageShareDenyNone, 0, false, null, out var stream);
        return stream;
    }

    private static IPreviewHandler CreatePreviewHandler(Guid handlerClassId)
    {
        var interfaceId = typeof(IPreviewHandler).GUID;
        CoCreateInstance(ref handlerClassId, IntPtr.Zero, ClsctxLocalServer, ref interfaceId, out var handler);
        return (IPreviewHandler)handler;
    }

    private static IShellItem CreateShellItem(string path)
    {
        var interfaceId = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out var item);
        return item;
    }

    private static bool TryFindPreviewHandler(string extension, out Guid handlerClassId)
    {
        handlerClassId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var extensionKey = extension.StartsWith('.') ? extension : "." + extension;
        var handler = ReadHandler(extensionKey);
        if (handler == null)
        {
            using var key = Registry.ClassesRoot.OpenSubKey(extensionKey);
            var programId = key?.GetValue(null) as string;
            handler = string.IsNullOrWhiteSpace(programId) ? null : ReadHandler(programId);
        }

        return handler != null && Guid.TryParse(handler, out handlerClassId);
    }

    private static string? ReadHandler(string className)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($"{className}\\shellex\\{PreviewHandlerCategory}");
        return key?.GetValue(null) as string;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateStreamOnFileEx(
        string fileName,
        uint mode,
        uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create,
        IStream? templateStream,
        out IStream stream);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object instance);

    [ComImport]
    [Guid(PreviewHandlerInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref NativeRect rect);
        void SetRect(ref NativeRect rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr hwnd);
        void TranslateAccelerator(ref NativeMessage message);
    }

    [ComImport]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
    }

    [ComImport]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(IStream stream, uint mode);
    }

    [ComImport]
    [Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithItem
    {
        void Initialize(IShellItem item, uint mode);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
    }

    [ComImport]
    [Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite
    {
        void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
        void GetSite(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object site);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;

        public static NativeRect Empty => new(0, 0, 0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

}

public sealed class PreviewFailureEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

[ComVisible(true)]
[Guid("fec87aaf-35f9-447a-adb7-20234491401a")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerFrame
{
    [PreserveSig]
    int GetWindowContext(out PreviewHandlerFrameInfo info);

    [PreserveSig]
    int TranslateAccelerator(IntPtr message);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class PreviewHandlerFrame(IntPtr window) : IPreviewHandlerFrame
{
    private readonly IntPtr _window = window;

    public int GetWindowContext(out PreviewHandlerFrameInfo info)
    {
        _ = GetClientRect(_window, out var frameRect);
        info = new PreviewHandlerFrameInfo
        {
            Size = (uint)Marshal.SizeOf<PreviewHandlerFrameInfo>(),
            Window = _window,
            FrameRect = frameRect,
        };
        return 0;
    }

    public int TranslateAccelerator(IntPtr message)
    {
        return 1;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out ShellPreviewHost.NativeRect rect);
}

[StructLayout(LayoutKind.Sequential)]
public struct PreviewHandlerFrameInfo
{
    public uint Size;
    public IntPtr Window;
    public ShellPreviewHost.NativeRect FrameRect;
    public IntPtr AcceleratorTable;
    public uint AcceleratorCount;
}
