using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DashFile.Services;

/// <summary>
/// 运行日志环形缓冲 + 落盘（Log\runtime.log）。
/// 崩溃日志会带上最近 20 条操作，事后能看出是"连接 / 删除 / 模块 / 插件"哪一步出的事。
/// </summary>
public static class AppLog
{
    private const int Keep = 80;
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static string? _lastRaw;
    private static DateTime _lastAt;

    public static string FilePath
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Log");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "runtime.log");
        }
    }

    public static void Add(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var raw = message.Trim();
        var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + raw;
        lock (Gate)
        {
            // 1 秒一次的状态轮询会不断产生同一条命令/同一句报错：
            // 60 秒内相同的行只落一次盘，避免把真正的操作记录挤出快照。
            if (_lastRaw == raw && (DateTime.Now - _lastAt).TotalSeconds < 60) return;
            _lastRaw = raw;
            _lastAt = DateTime.Now;

            Recent.Enqueue(line);
            while (Recent.Count > Keep) Recent.Dequeue();
        }
        try
        {
            var path = FilePath;
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                var old = path + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                try { File.Move(path, old, true); } catch { }
            }
            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    public static string[] Snapshot(int count)
    {
        lock (Gate)
        {
            var all = Recent.ToArray();
            if (all.Length <= count) return all;
            var result = new string[count];
            Array.Copy(all, all.Length - count, result, 0, count);
            return result;
        }
    }
}
