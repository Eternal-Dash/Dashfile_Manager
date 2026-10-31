using System;

namespace DashFile.Models;

public sealed class PosixEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public char TypeChar { get; init; } = '-';   // d - l c b p s
    public string Perms { get; init; } = "---------";
    public string Owner { get; init; } = "";
    public string Group { get; init; } = "";
    public string Selinux { get; init; } = "";
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string LinkTarget { get; init; } = "";

    public bool IsDir => TypeChar == 'd';
    public bool IsLink => TypeChar == 'l';
    public bool IsExec => Perms.Length == 9 && Perms[2] == 'x';

    /// <summary>是不是 zip 压缩包（小写比较，忽略结尾空格）</summary>
    public bool IsZip => !IsDir && !IsLink &&
        Name.TrimEnd().EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public string Kind => TypeChar switch
    {
        'd' => "目录",
        '-' => "文件",
        'l' => "链接",
        'c' => "字符设备",
        'b' => "块设备",
        'p' => "管道",
        's' => "套接字",
        _ => "其它"
    };

    public string DisplayName
    {
        get
        {
            if (IsLink && !string.IsNullOrEmpty(LinkTarget)) return $"{Name} → {LinkTarget}";
            return Name;
        }
    }

    public string SizeText
    {
        get
        {
            if (IsDir) return "";
            if (Size < 1024) return Size + " B";
            double v = Size;
            string[] u = { "KB", "MB", "GB", "TB" };
            int i = -1;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00") + " " + u[i];
        }
    }

    public string ModifiedText => Modified == DateTime.MinValue ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// 把 ls 的权限串还原成八进制。
    /// 以前把 s/S/t/T 一律当"有执行位"算，结果 setuid 文件显示 755 而不是 4755，
    ///   和权限对话框（PermissionDialog 正确解析特殊位）对不上。
    ///   小写 s/t = 特殊位 + 执行位；大写 S/T = 只有特殊位。
    /// </summary>
    public string ModeOctal
    {
        get
        {
            if (Perms.Length != 9) return "000";

            var special = 0;
            if (Perms[2] is 's' or 'S') special += 4;
            if (Perms[5] is 's' or 'S') special += 2;
            if (Perms[8] is 't' or 'T') special += 1;

            bool ExecAt(int i) => Perms[i] is 'x' or 's' or 't';

            int Triplet(int start)
            {
                var v = 0;
                if (Perms[start] == 'r') v += 4;
                if (Perms[start + 1] == 'w') v += 2;
                if (ExecAt(start + 2)) v += 1;
                return v;
            }

            var body = Triplet(0) * 100 + Triplet(3) * 10 + Triplet(6);
            return special > 0
                ? (special * 1000 + body).ToString("0000")
                : body.ToString("000");
        }
    }

    /// <summary>面向用户的类型描述（文件夹 / SH 脚本 / PNG 图片 …）</summary>
    public string TypeText
    {
        get
        {
            if (IsDir) return "文件夹";
            if (IsLink) return "符号链接";
            var n = Name.ToLowerInvariant();
            var ext = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : "";
            return ext switch
            {
                "sh" or "bash" or "zsh" => "SH 脚本",
                "txt" => "文本文档",
                "log" => "日志文件",
                "md" => "Markdown 文档",
                "json" => "JSON 文件",
                "xml" => "XML 文件",
                "ini" or "conf" or "cfg" or "prop" => "配置文件",
                "png" => "PNG 图片",
                "jpg" or "jpeg" => "JPEG 图片",
                "webp" => "WebP 图片",
                "gif" => "GIF 图片",
                "svg" => "SVG 矢量图",
                "mp4" => "MP4 视频",
                "mkv" => "MKV 视频",
                "mp3" => "MP3 音频",
                "wav" => "WAV 音频",
                "flac" => "FLAC 音频",
                "apk" => "APK 安装包",
                "apks" or "xapk" or "apkm" => "APK 合集",
                "zip" => "ZIP 压缩包",
                "rar" => "RAR 压缩包",
                "7z" => "7Z 压缩包",
                "tar" => "TAR 归档",
                "gz" or "tgz" => "GZ 压缩包",
                "so" => "共享库",
                "ko" => "内核模块",
                "img" => "镜像文件",
                "dex" => "DEX 字节码",
                "bin" => "二进制文件",
                "" => "文件",
                _ => ext.ToUpperInvariant() + " 文件",
            };
        }
    }

    /// <summary>精确字节数（属性面板用）</summary>
    public string SizeFull => IsDir ? "—" : $"{Size:N0} 字节（{SizeText}）";
}
