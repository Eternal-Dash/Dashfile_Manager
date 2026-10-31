using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DashFile.Models;

namespace DashFile.Controls;

/// <summary>按文件类型返回矢量图标（16x16 线性图标）</summary>
public sealed class FileIcon : IValueConverter
{
    public static readonly FileIcon Instance = new();

    private const string Folder = "M1.8,4.2 h3.9 l1.3,1.6 h7.2 v7.4 h-12.4 z";
    private const string File   = "M4.2,1.6 h4.8 l3.4,3.4 v9.4 h-8.2 z M9,1.6 v3.4 h3.4";
    private const string Text   = "M4.2,1.6 h4.8 l3.4,3.4 v9.4 h-8.2 z M5.8,7.4 h4.4 M5.8,9.8 h4.4 M5.8,12.2 h2.6";
    private const string Script = "M4.2,1.6 h4.8 l3.4,3.4 v9.4 h-8.2 z M5.6,8.2 l1.4,1.4 -1.4,1.4 M8.4,11 h2.2";
    private const string Image  = "M2,3.2 h12 v9.6 h-12 z M4.6,6 a1.1,1.1 0 1,0 0.1,0 M3,12 l3.4,-3.4 2.2,2.2 1.9,-1.9 2.9,2.9";
    private const string Video  = "M2,3.6 h12 v9.2 h-12 z M6.4,6.2 l3.8,2.4 -3.8,2.4 z";
    private const string Audio  = "M6,11.4 a1.4,1.4 0 1,0 0.1,0 M7.4,11 V4.4 l4.8,-1.4 v6.4 M10.8,12.2 a1.4,1.4 0 1,0 0.1,0";
    private const string Apk    = "M3.2,2 h9.6 v12 h-9.6 z M5.4,6 h5.2 M5.4,9 h5.2 M5.4,12 h2.6";
    private const string Archive= "M3,3 h10 v10.4 h-10 z M6.6,3 v2 M9.4,3 v2 M6.6,6.4 v2 M9.4,6.4 v2";
    private const string Link   = "M6.6,10.4 l-1,1 a2.1,2.1 0 0,1 -3,-3 l2,-2 a2.1,2.1 0 0,1 3,0 M9.4,5.6 l1,-1 a2.1,2.1 0 0,1 3,3 l-2,2 a2.1,2.1 0 0,1 -3,0";
    private const string Binary = "M4,4 h3 v3 h-3 z M9,9 h3 v3 h-3 z M4,9 h3 M9,4 h3";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PosixEntry e) return Geometry.Parse(File);
        return Geometry.Parse(Pick(e));
    }

    private static string Pick(PosixEntry e)
    {
        if (e.IsDir) return Folder;
        if (e.IsLink) return Link;

        var n = e.Name.ToLowerInvariant();
        var ext = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : "";

        return ext switch
        {
            "sh" or "bash" or "zsh" => Script,
            "txt" or "log" or "md" or "json" or "xml" or "ini" or "conf" or "cfg" or "prop" => Text,
            "png" or "jpg" or "jpeg" or "webp" or "bmp" or "gif" or "svg" => Image,
            "mp4" or "mkv" or "avi" or "mov" or "webm" or "3gp" => Video,
            "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" => Audio,
            "apk" or "apks" or "xapk" or "apkm" => Apk,
            "zip" or "rar" or "7z" or "tar" or "gz" or "xz" or "bz2" or "tgz" => Archive,
            "so" or "ko" or "bin" or "elf" or "img" or "dex" or "oat" or "vdex" => Binary,
            _ => File,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
