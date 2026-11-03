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

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => IconCatalog.Get(value is PosixEntry entry ? Pick(entry) : "file");

    private static string Pick(PosixEntry e)
    {
        if (e.IsDir) return "folder";
        if (e.IsLink) return "shortcut";

        var n = e.Name.ToLowerInvariant();
        var ext = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : "";

        return ext switch
        {
            "sh" or "bash" or "zsh" => "script",
            "txt" or "log" or "md" or "json" or "xml" or "ini" or "conf" or "cfg" or "prop" => "text",
            "png" or "jpg" or "jpeg" or "webp" or "bmp" or "gif" or "svg" => "image",
            "mp4" or "mkv" or "avi" or "mov" or "webm" or "3gp" => "video",
            "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" => "audio",
            "apk" or "apks" or "xapk" or "apkm" => "android",
            "zip" or "rar" or "7z" or "tar" or "gz" or "xz" or "bz2" or "tgz" => "archive",
            "so" or "ko" or "bin" or "elf" or "img" or "dex" or "oat" or "vdex" => "chip",
            _ => "file",
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
