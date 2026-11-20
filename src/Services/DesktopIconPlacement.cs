using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;

namespace DashFile.Services;

/// <summary>Positions only the filesystem items the current desktop export wrote.</summary>
[SupportedOSPlatform("windows")]
internal static class DesktopIconPlacement
{
    internal enum Result { Positioned, AutoArrange, Unavailable }

    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private const int DesktopClass = 8;
    private const int NeedDispatch = 1;
    private const uint AllViewItems = 2;
    private const uint FileSystemPath = 0x80058000;
    private const uint PositionAtScreenPoint = 0x80 | 0x20;

    public static async Task<Result> PlaceAsync(IReadOnlyList<string> paths, PixelPoint releasePoint, PixelRect? workArea)
    {
        if (paths.Count == 0) return Result.Positioned;

        object? windows = null, desktop = null, browser = null, shellView = null;
        IntPtr desktopId = IntPtr.Zero;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!);
            object location = 0;
            object reserved = Type.Missing;
            desktop = ((dynamic)windows!).FindWindowSW(ref location, ref reserved, DesktopClass, out int _, NeedDispatch);
            if (desktop is not IServiceProvider provider) return Result.Unavailable;

            var service = TopLevelBrowser;
            var browserInterface = typeof(IShellBrowser).GUID;
            if (provider.QueryService(ref service, ref browserInterface, out browser) < 0 ||
                browser is not IShellBrowser shellBrowser ||
                shellBrowser.QueryActiveShellView(out shellView) < 0 ||
                shellView is not IFolderView folder)
                return Result.Unavailable;

            if (folder.GetAutoArrange() == 0) return Result.AutoArrange;
            if (SHGetSpecialFolderLocation(IntPtr.Zero, 0, out desktopId) < 0)
                return Result.Unavailable;

            folder.GetSpacing(out var spacing);
            var pending = new Dictionary<string, ScreenPoint>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var path in paths)
            {
                if (pending.TryAdd(Path.GetFullPath(path), Offset(releasePoint, workArea, spacing, index)))
                    index++;
            }

            // Explorer can receive the filesystem change notification after the download task finishes.
            for (int attempt = 0; pending.Count > 0 && attempt < 8; attempt++)
            {
                if (folder.GetAutoArrange() == 0) return Result.AutoArrange;
                PlaceVisibleItems(folder, desktopId, pending);
                if (pending.Count > 0) await Task.Delay(120);
            }

            return pending.Count == 0 ? Result.Positioned : Result.Unavailable;
        }
        catch (Exception ex)
        {
            AppLog.Add("读取桌面图标布局失败：" + ex.Message);
            return Result.Unavailable;
        }
        finally
        {
            if (desktopId != IntPtr.Zero) ILFree(desktopId);
            Release(shellView);
            Release(browser);
            Release(desktop);
            Release(windows);
        }
    }

    private static void PlaceVisibleItems(IFolderView folder, IntPtr desktopId, Dictionary<string, ScreenPoint> pending)
    {
        if (folder.ItemCount(AllViewItems, out var count) < 0) return;
        for (var i = 0; i < count && pending.Count > 0; i++)
        {
            if (folder.Item(i, out var child) < 0 || child == IntPtr.Zero) continue;
            IntPtr absolute = IntPtr.Zero, name = IntPtr.Zero;
            try
            {
                absolute = ILCombine(desktopId, child);
                if (absolute == IntPtr.Zero || SHGetNameFromIDList(absolute, FileSystemPath, out name) < 0) continue;
                var path = Marshal.PtrToStringUni(name);
                if (path != null && pending.TryGetValue(path, out var point) &&
                    folder.SelectAndPositionItems(1, ref child, ref point, PositionAtScreenPoint) >= 0)
                    pending.Remove(path);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
                if (absolute != IntPtr.Zero) ILFree(absolute);
                ILFree(child);
            }
        }
    }

    private static ScreenPoint Offset(PixelPoint origin, PixelRect? area, ScreenPoint spacing, int index)
    {
        if (index == 0 || area is not { } work) return new ScreenPoint { X = origin.X, Y = origin.Y };
        var width = Math.Max(48, spacing.X);
        var height = Math.Max(48, spacing.Y);
        var columns = Math.Max(1, Math.Min(4, work.Width / width));
        var x = origin.X + index % columns * width;
        if (x >= work.Right) x -= columns * width;
        var y = origin.Y + index / columns * height;
        if (y >= work.Bottom) y = work.Y + (y - work.Y) % Math.Max(1, work.Height);
        return new ScreenPoint { X = Math.Clamp(x, work.X, work.Right - 1), Y = y };
    }

    private static void Release(object? instance)
    {
        if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X; public int Y; }

    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(IntPtr owner, int folder, out IntPtr item);
    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr absolute, IntPtr relative);
    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr item);
    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr item, uint format, out IntPtr name);

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // These slots precede QueryActiveShellView in the native IShellBrowser vtable.
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode();
        void SetCurrentViewMode();
        void GetFolder();
        [PreserveSig] int Item(int index, out IntPtr item);
        [PreserveSig] int ItemCount(uint flags, out int count);
        void Items();
        void GetSelectionMarkedItem();
        void GetFocusedItem();
        void GetItemPosition();
        [PreserveSig] int GetSpacing(out ScreenPoint spacing);
        void GetDefaultSpacing();
        [PreserveSig] int GetAutoArrange();
        void SelectItem();
        [PreserveSig] int SelectAndPositionItems(uint count, ref IntPtr item, ref ScreenPoint point, uint flags);
    }
}
