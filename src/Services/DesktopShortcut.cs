using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DashFile.Services;

internal static class DesktopShortcut
{
    public static void Create()
    {
        if (!OperatingSystem.IsWindows()) return;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop) || string.IsNullOrEmpty(executable) ||
            string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("无法确定桌面位置或 DashFile.exe 路径。");

        var destination = Path.Combine(desktop, AppInfo.Name + ".lnk");
        CreateLink(destination, executable);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void CreateLink(string destination, string executable)
    {
        if (File.Exists(destination)) return;
        var desktop = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(desktop, ".dashfile-" + Guid.NewGuid().ToString("N") + ".lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(temporary);
            try
            {
                link.TargetPath = executable;
                link.WorkingDirectory = Path.GetDirectoryName(executable);
                link.Description = AppInfo.Product;
                link.Save();
            }
            finally { Marshal.ReleaseComObject(link); }

            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
