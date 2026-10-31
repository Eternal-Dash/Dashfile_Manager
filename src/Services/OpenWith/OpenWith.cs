using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DashFile.Services.OpenWith;

/// <summary>「打开方式」里的动作。顺序 = 菜单分组顺序。</summary>
public enum FileAction
{
    EditText,
    XmlInfo,
    SvgInfo,
    ImagePreview,
    MusicPlay,
    VideoPlay,
    FontPreview,
    ArchiveBrowse,
    JarInfo,
    ApkInfo,
    ApksInfo,
    SignatureInfo,
    HexView,
    DexInfo,
    ArscInfo,
    ClassInfo,
    NinePatchPreview,
    ScriptRun,
}

/// <summary>文件类型 → 默认动作，以及各种“查看”用的轻量解析。</summary>
public static class OpenWith
{
    public static string Title(FileAction a) => a switch
    {
        FileAction.EditText => "编辑文本",
        FileAction.XmlInfo => "Xml功能",
        FileAction.SvgInfo => "Svg功能",
        FileAction.ImagePreview => "查看图片",
        FileAction.MusicPlay => "播放音乐",
        FileAction.VideoPlay => "播放视频",
        FileAction.FontPreview => "查看字体",
        FileAction.ArchiveBrowse => "浏览压缩包",
        FileAction.JarInfo => "Jar功能",
        FileAction.ApkInfo => "Apk信息",
        FileAction.ApksInfo => "Apks信息",
        FileAction.SignatureInfo => "导入签名",
        FileAction.HexView => "十六进制",
        FileAction.DexInfo => "Dex功能",
        FileAction.ArscInfo => "Arsc功能",
        FileAction.ClassInfo => "Class功能",
        FileAction.NinePatchPreview => "点九编辑",
        FileAction.ScriptRun => "linux脚本",
        _ => "打开方式",
    };

    /// <summary>菜单图标 key（对应 MenuIcons 里的路径）。</summary>
    public static string IconKey(FileAction a) => a switch
    {
        FileAction.EditText => "text",
        FileAction.XmlInfo => "xml",
        FileAction.SvgInfo => "svg",
        FileAction.ImagePreview => "image",
        FileAction.MusicPlay => "music",
        FileAction.VideoPlay => "video",
        FileAction.FontPreview => "font",
        FileAction.ArchiveBrowse => "archive",
        FileAction.JarInfo => "jar",
        FileAction.ApkInfo => "apk",
        FileAction.ApksInfo => "apks",
        FileAction.SignatureInfo => "sign",
        FileAction.HexView => "hex",
        FileAction.DexInfo => "dex",
        FileAction.ArscInfo => "arsc",
        FileAction.ClassInfo => "class",
        FileAction.NinePatchPreview => "nine",
        FileAction.ScriptRun => "terminal",
        _ => "dot",
    };

    /// <summary>每项一个独立颜色（图标描边）。</summary>
    public static string Color(FileAction a) => a switch
    {
        FileAction.EditText => "#7FB2FF",
        FileAction.XmlInfo => "#E3C46B",
        FileAction.SvgInfo => "#F3D06B",
        FileAction.ImagePreview => "#62D6A8",
        FileAction.MusicPlay => "#6FD3E8",
        FileAction.VideoPlay => "#8FB5FF",
        FileAction.FontPreview => "#D6A8FF",
        FileAction.ArchiveBrowse => "#F0B35B",
        FileAction.JarInfo => "#D0A36B",
        FileAction.ApkInfo => "#7BD88F",
        FileAction.ApksInfo => "#8FE0B0",
        FileAction.SignatureInfo => "#FF9BB3",
        FileAction.HexView => "#B6A6FF",
        FileAction.DexInfo => "#FF8E8E",
        FileAction.ArscInfo => "#FFB07C",
        FileAction.ClassInfo => "#FF9E7A",
        FileAction.NinePatchPreview => "#9ED6B8",
        FileAction.ScriptRun => "#C7E36B",
        _ => "#9FA1AA",
    };

    public static string Ext(string name)
    {
        var i = name.LastIndexOf('.');
        return i < 0 || i == name.Length - 1 ? "" : name[(i + 1)..].ToLowerInvariant();
    }

    /// <summary>左键双击的默认动作；返回 null 表示无合适工具，应弹出「打开方式」。</summary>
    public static FileAction? DefaultFor(string name)
    {
        var ext = Ext(name);
        if (name.EndsWith(".9.png", StringComparison.OrdinalIgnoreCase)) return FileAction.NinePatchPreview;
        return ext switch
        {
            "txt" or "log" or "md" or "ini" or "conf" or "cfg" or "prop" or "json"
                or "py" or "lua" or "js" or "css" or "html" or "htm"
                => FileAction.EditText,
            "sh" or "bash" or "zsh" or "run" => FileAction.ScriptRun,
            "xml" => FileAction.XmlInfo,
            "svg" => FileAction.SvgInfo,
            "png" or "jpg" or "jpeg" or "webp" or "bmp" or "gif" => FileAction.ImagePreview,
            "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" => FileAction.MusicPlay,
            "mp4" or "mkv" or "avi" or "mov" or "webm" or "3gp" => FileAction.VideoPlay,
            "ttf" or "otf" or "ttc" => FileAction.FontPreview,
            "zip" or "7z" or "rar" or "tar" or "gz" or "xz" or "bz2" or "tgz" => FileAction.ArchiveBrowse,
            "jar" => FileAction.JarInfo,
            "apk" => FileAction.ApkInfo,
            "apks" or "apkm" or "xapk" => FileAction.ApksInfo,
            "dex" => FileAction.DexInfo,
            "arsc" => FileAction.ArscInfo,
            "class" => FileAction.ClassInfo,
            _ => null,
        };
    }

    // ---------------- 轻量查看器 ----------------

    public static string HexDump(byte[] data, int limit = 256 * 1024)
    {
        var n = Math.Min(data.Length, limit);
        var sb = new StringBuilder(n * 4);
        for (var off = 0; off < n; off += 16)
        {
            sb.Append(off.ToString("x8")).Append("  ");
            var line = Math.Min(16, n - off);
            for (var i = 0; i < 16; i++)
            {
                sb.Append(i < line ? data[off + i].ToString("x2") : "  ");
                sb.Append(i == 7 ? "  " : " ");
            }
            sb.Append(" ");
            for (var i = 0; i < line; i++)
            {
                var b = data[off + i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine();
        }
        if (data.Length > limit) sb.AppendLine($"…（仅显示前 {limit / 1024} KB，共 {data.Length} 字节）");
        return sb.ToString();
    }

    public static string TextReport(byte[] data, int limit = 2 * 1024 * 1024)
    {
        var n = Math.Min(data.Length, limit);
        var text = Encoding.UTF8.GetString(data, 0, n).Replace("\0", "");
        if (data.Length > limit) text += $"\n\n…（仅显示前 {limit / 1024} KB，共 {data.Length} 字节）";
        return text;
    }

    public static string XmlReport(byte[] data)
    {
        // 二进制 AndroidManifest.xml → 解码成文本；普通 xml 直接当文本
        var root = AxmlReader.Parse(data);
        if (root != null) return root.ToXml();
        return TextReport(data);
    }

    public static string DexReport(byte[] data, string name)
    {
        var stat = Dex.ReadStat(name, data);
        var sb = new StringBuilder();
        sb.AppendLine($"DEX 文件：{name}");
        sb.AppendLine($"大小      {stat.Size:N0} 字节");
        sb.AppendLine($"版本      dex {stat.Version}.0");
        sb.AppendLine($"strings   {stat.StringIds:N0}");
        sb.AppendLine($"types     {stat.TypeIds:N0}");
        sb.AppendLine($"protos    {stat.ProtoIds:N0}");
        sb.AppendLine($"fields    {stat.FieldIds:N0}");
        sb.AppendLine($"methods   {stat.MethodIds:N0}");
        sb.AppendLine($"classes   {stat.ClassDefs:N0}");
        var strings = Dex.ReadStrings(data);
        sb.AppendLine();
        sb.AppendLine($"字符串（前 500 条 / 共 {strings.Count} 条）：");
        foreach (var s in strings.Take(500)) sb.AppendLine("  " + s);
        return sb.ToString();
    }

    public static string ArscReport(byte[] data)
    {
        var strings = Arsc.ExtractStrings(data);
        var sb = new StringBuilder();
        sb.AppendLine($"resources.arsc：{data.Length:N0} 字节");
        sb.AppendLine($"全局字符串池：{strings.Count:N0} 条");
        sb.AppendLine();
        foreach (var s in strings.Take(2000)) sb.AppendLine(s);
        if (strings.Count > 2000) sb.AppendLine($"…（仅显示前 2000 条）");
        return sb.ToString();
    }

    public static string ClassReport(byte[] data)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Class 文件：{data.Length:N0} 字节");
        if (data.Length >= 8 && data[0] == 0xCA && data[1] == 0xFE && data[2] == 0xBA && data[3] == 0xBE)
        {
            var minor = (data[4] << 8) | data[5];
            var major = (data[6] << 8) | data[7];
            sb.AppendLine($"Magic     CAFEBABE");
            sb.AppendLine($"版本      {major}.{minor}");
            sb.AppendLine();
            sb.AppendLine("可打印字符串（前 300 条）：");
            foreach (var s in ExtractStrings(data, 4).Take(300)) sb.AppendLine("  " + s);
        }
        else
        {
            sb.AppendLine("（不是标准 class 文件，下面按十六进制查看）");
        }
        sb.AppendLine();
        sb.AppendLine(HexDump(data, 64 * 1024));
        return sb.ToString();
    }

    public static string ZipReport(string localPath, string title)
    {
        var sb = new StringBuilder();
        try
        {
            using var zip = ZipFile.OpenRead(localPath);
            sb.AppendLine($"{title}：{zip.Entries.Count} 个条目");
            sb.AppendLine();
            foreach (var e in zip.Entries.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).Take(3000))
                sb.AppendLine($"{e.Length,12:N0}  {e.FullName}");
            if (zip.Entries.Count > 3000) sb.AppendLine("…（仅显示前 3000 条）");
        }
        catch (Exception ex) { sb.AppendLine("读取失败：" + ex.Message); }
        return sb.ToString();
    }

    public static string ApkReport(string localPath, bool signatureOnly = false)
    {
        var a = ApkAnalysis.Build(localPath);
        if (signatureOnly) return a.SignatureReport();
        var sb = new StringBuilder();
        sb.Append(a.OverviewReport());
        sb.AppendLine();
        sb.AppendLine("── 组件 ──");
        sb.Append(a.ComponentsReport());
        sb.AppendLine();
        sb.AppendLine("── 签名 ──");
        sb.Append(a.SignatureReport());
        sb.AppendLine();
        sb.AppendLine("── DEX ──");
        sb.Append(a.DexReport());
        if (!string.IsNullOrWhiteSpace(a.ManifestXml))
        {
            sb.AppendLine();
            sb.AppendLine("── AndroidManifest.xml ──");
            var xml = a.ManifestXml;
            sb.AppendLine(xml.Length > 40000 ? xml[..40000] + "\n…（已截断）" : xml);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> ExtractStrings(byte[] data, int minLen)
    {
        var sb = new StringBuilder();
        foreach (var b in data)
        {
            if (b >= 32 && b < 127) { sb.Append((char)b); continue; }
            if (sb.Length >= minLen && sb.Length <= 300) yield return sb.ToString();
            sb.Clear();
        }
    }
}
