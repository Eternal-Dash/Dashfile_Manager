using System;
using System.IO;

namespace DashFile.Services;

/// <summary>
/// 配置 / 缓存写入：先写 .tmp，再原子替换。避免写到一半崩溃/断电后留下半截 json，
/// 下次启动反序列化失败把设置清空。
/// </summary>
public static class AtomicFile
{
    // 同一个文件可能被 UI/后台同时写（设置、标签缓存）：串行化 + 每次用唯一临时名，
    // 避免两个写入者抢同一个 .tmp。
    private static readonly object Gate = new();

    public static void WriteAllText(string path, string text)
        => WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(text));

    public static void WriteAllBytes(string path, byte[] data)
    {
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, data);
                Replace(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

    private static void Replace(string tmp, string path)
    {
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, null); return; }
            catch { /* 被占用/跨卷时退回 Move */ }
        }
        File.Move(tmp, path, true);
    }
}
