using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DashFile.Models;

namespace DashFile.Controls;

/// <summary>按文件类型返回矢量图标（16x16 线性图标）</summary>
public sealed class FileIcon : IValueConverter
{
    public static readonly FileIcon Instance = new();

    private const string Folder = "M2.5,3.5 h3.6 l1.5,2 h5.9 a1,1 0 0 1 1,1 v6 a1,1 0 0 1 -1,1 h-11 a1,1 0 0 1 -1,-1 v-8 a1,1 0 0 1 1,-1 z";
    private const string File   = "M4.5,1.5 h4.5 l3.5,3.5 v8.5 a1,1 0 0 1 -1,1 h-7 a1,1 0 0 1 -1,-1 v-11 a1,1 0 0 1 1,-1 z M9,1.5 v3.5 h3.5";
    private const string Text   = "M4.2,1.6 h4.8 l3.4,3.4 v9.4 h-8.2 z M5.8,7.4 h4.4 M5.8,9.8 h4.4 M5.8,12.2 h2.6";
    private const string Script = "M4.2,1.6 h4.8 l3.4,3.4 v9.4 h-8.2 z M5.6,8.2 l1.4,1.4 -1.4,1.4 M8.4,11 h2.2";
    private const string Image  = "M2.5,2.5 h11 a1,1 0 0 1 1,1 v9 a1,1 0 0 1 -1,1 h-11 a1,1 0 0 1 -1,-1 v-9 a1,1 0 0 1 1,-1 z M4,6 a1,1 0 1 0 2,0 a1,1 0 1 0 -2,0 M2,12 l4,-4 2.5,2.5 2,-2 3.5,3.5";
    private const string Video  = "M2,3.6 h12 v9.2 h-12 z M6.4,6.2 l3.8,2.4 -3.8,2.4 z";
    private const string Audio  = "M3,11.5 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M7,11.5 v-8 l6,-1.5 v8 M9,10 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M7,6 l6,-1.5";
    private const string Apk    = "M4,6 h8 v6 h-8 z M4,6 a4,4 0 0 1 8,0 M5,2 l-1,-1 M11,2 l1,-1 M2,7 v4 M14,7 v4 M6,12 v2 M10,12 v2 M6,4 v0.2 M10,4 v0.2";
    private const string Archive= "M4,1.5 h8 a1,1 0 0 1 1,1 v11 a1,1 0 0 1 -1,1 h-8 a1,1 0 0 1 -1,-1 v-11 a1,1 0 0 1 1,-1 z M7,2 v2 h2 v2 h-2 v2 h2 v2 M7,11 h2 v2 h-2 z";
    private const string Link   = "M6.6,10.4 l-1,1 a2.1,2.1 0 0,1 -3,-3 l2,-2 a2.1,2.1 0 0,1 3,0 M9.4,5.6 l1,-1 a2.1,2.1 0 0,1 3,3 l-2,2 a2.1,2.1 0 0,1 -3,0";
    private const string Binary = "M5,4 h6 a1,1 0 0 1 1,1 v6 a1,1 0 0 1 -1,1 h-6 a1,1 0 0 1 -1,-1 v-6 a1,1 0 0 1 1,-1 z M6.5,6.5 h3 v3 h-3 z M6,1.5 v2.5 M10,1.5 v2.5 M6,12 v2.5 M10,12 v2.5 M1.5,6 h2.5 M1.5,10 h2.5 M12,6 h2.5 M12,10 h2.5";

    // Eleven immutable paths shared by all realised rows, rather than parsing on every binding.
    private static readonly IReadOnlyDictionary<string, Geometry> Geometries =
        new[] { Folder, File, Text, Script, Image, Video, Audio, Apk, Archive, Link, Binary }
        .Distinct().ToDictionary(path => path, Geometry.Parse);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Geometries[value is PosixEntry entry ? Pick(entry) : File];

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
