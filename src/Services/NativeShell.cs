using System;
using System.Runtime.InteropServices;

namespace DashFile.Services;

/// <summary>
/// 调 Windows 原生 Shell 复制，目的就是让系统弹出那个“正在复制…”的进度窗口，
/// 让用户能看到真实的传输过程（而不是我们悄悄拷完）。
/// </summary>
public static class NativeShell
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string? pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private const uint FO_COPY = 0x0002;

    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SIMPLEPROGRESS = 0x0100;  // 显示进度条
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>把若干本地文件/文件夹复制到目标目录，并显示系统进度窗口</summary>
    public static bool CopyWithProgress(IntPtr owner, string[] sources, string targetDir, string? title = null)
    {
        if (sources.Length == 0) return false;

        // 多字符串：每个路径以 \0 结尾，整体再补一个 \0
        var from = string.Join("\0", sources) + "\0\0";

        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_COPY,
            pFrom = from,
            pTo = targetDir + "\0\0",
            fFlags = (ushort)(FOF_SIMPLEPROGRESS | FOF_NOCONFIRMATION | FOF_NOERRORUI),
            lpszProgressTitle = title ?? "正在复制"
        };

        try { return SHFileOperation(ref op) == 0; }
        catch { return false; }
    }
}
