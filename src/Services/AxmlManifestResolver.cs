using System.Collections.Generic;

namespace DashFile.Services;

/// <summary>
/// 二进制 AndroidManifest.xml 的读取入口（取 &lt;application&gt; 的 icon / roundIcon、遍历属性）。
///
/// 这里曾经是一份独立的 AXML 扫描器：和 OpenWith/ApkAnalysis 里的解析器、
///   VectorDrawableRenderer 里的解析器是三份各自演化的实现，越界保护与字符串解码各写各的，
///   修一份永远漏两份。现在全部收敛到 <see cref="AxmlReader"/>，本类只保留对外签名，
///   调用点（AppService 的取图标流程）不用改。
/// </summary>
public static class AxmlManifestResolver
{
    /// <summary>列出 XML 里所有属性（元素名 + 属性名 + 类型 + 原始值 + 字符串值），用于遍历 layer-list 等</summary>
    public static List<(string elem, string attr, byte dataType, uint data, string? str)> AllAttrs(byte[] axml)
        => AxmlReader.AllAttrs(axml);

    /// <summary>在任意 AXML 里查找某元素的某个属性（返回资源 ID 或字符串）</summary>
    public static (uint resId, string? str) FindAttr(byte[] axml, string element, string attr)
        => AxmlReader.FindAttr(axml, element, attr);

    /// <summary>返回 (资源ID, 直接路径)；都没有则 (0, null)</summary>
    public static (uint resId, string? directPath) ResolveIcon(byte[] axml)
        => AxmlReader.ResolveIcon(axml);
}
