using System;
using System.IO;

namespace DashFile.Services;

public static class AdbLocator
{
    /// <summary>优先使用 exe 同目录下的 adb.exe（便携包），否则回退到 PATH</summary>
    public static string Resolve()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            foreach (var d in new[]
            {
                Path.Combine(dir, "Binaries", "Adb"),
                Path.Combine(dir, "Binaries"),
                dir,
                Path.Combine(dir, "tools"),
            })
            {
                var exe = Path.Combine(d, "adb.exe");
                if (File.Exists(exe)) return exe;
                var bin = Path.Combine(d, "adb");
                if (File.Exists(bin)) return bin;
            }
        }
        catch { /* ignore */ }
        return "adb";
    }
}
