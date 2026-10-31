using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DashFile.Services;

/// <summary>
/// 二进制 Android XML（AXML）与 res 字符串池的**唯一**实现。
///
/// 背景：这个项目里曾经有三份各自演化的 AXML 解析器 ——
///   · OpenWith/ApkAnalysis.cs 的 Axml + StringPool（解析成整棵树，给 APK 信息页用）
///   · Services/AxmlManifestResolver.cs（单遍扫描，给"取图标"用）
///   · Services/VectorDrawableRenderer.cs（单遍扫描，自己分类成 refs/strs/floats/colors）
/// 三份的越界保护、字符串解码、类型格式化各写各的，修一份永远漏两份
/// （VectorDrawableRenderer 的负索引就是这样漏掉的）。现在统一到这里：
///   · 所有 U16/U32 都做边界检查，任何位置都不会抛 IndexOutOfRange；
///   · 字符串池只有一套解码（UTF-8 双长度前缀 + UTF-16 代理长度都处理）；
///   · 树深度与元素数量都有上限，畸形输入不会把栈打穿。
/// 三份旧实现现在只剩薄封装，行为由这里单点决定。
/// </summary>
internal static class AxmlReader
{
    internal const ushort ResXmlType = 0x0003;
    internal const ushort ResStringPoolType = 0x0001;
    internal const ushort ResXmlStartElement = 0x0102;
    internal const ushort ResXmlEndElement = 0x0103;

    /// <summary>元素最大嵌套层数：畸形/恶意 XML 可以套几千层，递归渲染/序列化会 StackOverflow（不可捕获）</summary>
    private const int MaxDepth = 256;

    /// <summary>单份 XML 最多解析多少个元素（防止 20 万个空标签把内存吃光）</summary>
    private const int MaxElements = 200_000;

    /// <summary>字符串池条数上限（正常 APK 的 manifest 池几百条，几 MB 的 arsc 也就几万条）</summary>
    private const int MaxStrings = 1_000_000;

    // ---------------- 基础读取（全部带边界检查） ----------------

    internal static ushort U16(byte[] d, int p)
        => p >= 0 && p + 2 <= d.Length ? (ushort)(d[p] | (d[p + 1] << 8)) : (ushort)0;

    internal static uint U32(byte[] d, int p)
        => p >= 0 && p + 4 <= d.Length
            ? (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24))
            : 0u;

    internal static byte U8(byte[] d, int p) => p >= 0 && p < d.Length ? d[p] : (byte)0;

    // ---------------- 字符串池 ----------------

    /// <summary>解析一个 RES_STRING_POOL chunk（返回空列表表示格式不对，调用方按"没有字符串"处理）</summary>
    internal static List<string> ReadStringPool(byte[] d, int offset)
    {
        var list = new List<string>();
        if (offset < 0 || offset + 28 > d.Length) return list;

        int headerSize = U16(d, offset + 2);
        int count = (int)U32(d, offset + 8);
        int flags = (int)U32(d, offset + 16);
        int stringsStart = (int)U32(d, offset + 20);
        if (count <= 0 || count > MaxStrings) return list;

        bool utf8 = (flags & 0x100) != 0;
        int offsetsPos = offset + headerSize;

        for (var i = 0; i < count; i++)
        {
            int off = (int)U32(d, offsetsPos + i * 4);
            int p = offset + stringsStart + off;
            if (p <= 0 || p >= d.Length) { list.Add(""); continue; }
            list.Add(utf8 ? ReadUtf8(d, p) : ReadUtf16(d, p));
        }
        return list;
    }

    /// <summary>
    /// UTF-8 串：先 1~2 字节的 UTF-16 长度，再 1~2 字节的字节长度，再是内容。
    /// 两步都要读，用字节长度截断（有些实现只读第一个长度，遇到非 ASCII 会截断成半个字符）。
    /// </summary>
    private static string ReadUtf8(byte[] d, int p)
    {
        int i = p;
        var b0 = U8(d, i++);
        if ((b0 & 0x80) != 0) i++;                       // UTF-16 长度（用不到，但要跳过去）

        int len = U8(d, i++);
        if ((len & 0x80) != 0) len = ((len & 0x7f) << 8) | U8(d, i++);

        if (len < 0 || i + len > d.Length) len = Math.Max(0, d.Length - i);
        return len <= 0 ? "" : Encoding.UTF8.GetString(d, i, len);
    }

    /// <summary>UTF-16 串：长度（可能 1 或 2 个 u16，最高位是"后面还有一半长度"的标志），再是内容</summary>
    private static string ReadUtf16(byte[] d, int p)
    {
        int i = p;
        int len = U16(d, i); i += 2;
        if ((len & 0x8000) != 0)
        {
            len = ((len & 0x7fff) << 16) | U16(d, i);
            i += 2;
        }
        int bytes = len * 2;
        if (bytes < 0 || i + bytes > d.Length) bytes = Math.Max(0, d.Length - i);
        return bytes <= 0 ? "" : Encoding.Unicode.GetString(d, i, bytes);
    }

    // ---------------- 文档解析 ----------------

    /// <summary>解析 AXML；不是 AXML 或结构坏掉时返回 null（不抛异常）</summary>
    internal static AxmlElement? Parse(byte[]? data)
    {
        try { return ParseCore(data); }
        catch { return null; }   // 任何一处畸形都不该让"看个图标"崩掉调用方
    }

    private static AxmlElement? ParseCore(byte[]? data)
    {
        if (data == null || data.Length < 8) return null;
        if (U16(data, 0) != ResXmlType) return null;

        int headerSize = U16(data, 2);
        int total = (int)U32(data, 4);
        if (total <= 0 || total > data.Length) total = data.Length;

        var pool = new List<string>();
        AxmlElement? root = null;
        var stack = new Stack<AxmlElement>();
        var elements = 0;

        int pos = headerSize;
        while (pos + 8 <= total)
        {
            int chunkType = U16(data, pos);
            int chunkSize = (int)U32(data, pos + 4);
            if (chunkSize <= 0 || pos + chunkSize > total) break;   // 长度自相矛盾：后面的都不信了

            if (chunkType == ResStringPoolType)
            {
                pool = ReadStringPool(data, pos);
            }
            else if (chunkType == ResXmlStartElement)
            {
                if (++elements > MaxElements) break;

                var el = ReadElement(data, pos, chunkSize, pool);
                if (el != null)
                {
                    if (stack.Count > 0) stack.Peek().Children.Add(el);
                    else root ??= el;

                    if (stack.Count < MaxDepth) stack.Push(el);
                    else return root;                    // 太深了：停在已经解析出的部分
                }
            }
            else if (chunkType == ResXmlEndElement)
            {
                if (stack.Count > 0) stack.Pop();
            }

            pos += chunkSize;
        }

        return root;
    }

    private static AxmlElement? ReadElement(byte[] d, int pos, int chunkSize, List<string> pool)
    {
        // 结构：chunk 头 8 + lineNumber 4 + comment 4 = 16，然后 ns 4 / name 4 /
        //        attributeStart 2 / attributeSize 2 / attributeCount 2 / id 2 / class 2 / style 2
        int nameIdx = (int)U32(d, pos + 20);
        var el = new AxmlElement { Name = StrOf(pool, nameIdx) };

        int attrStart = U16(d, pos + 24);
        int attrCount = U16(d, pos + 28);
        int aBase = pos + 16 + attrStart;
        int chunkEnd = pos + chunkSize;

        for (var i = 0; i < attrCount; i++)
        {
            int ap = aBase + i * 20;
            if (ap < 0 || ap + 20 > chunkEnd || ap + 20 > d.Length) break;

            int aNameIdx = (int)U32(d, ap + 4);
            var aName = StrOf(pool, aNameIdx);
            // xmlns 声明与空名对"取图标/看清单"没有任何用处，统一在这里滤掉（三份旧实现里有两份也这么做）
            if (aName.Length == 0 || aName.StartsWith("xmlns", StringComparison.Ordinal)) continue;

            byte dataType = U8(d, ap + 15);
            uint rawValue = U32(d, ap + 8);
            uint value = U32(d, ap + 16);
            string? str = dataType == 0x03 ? StrOf(pool, (int)value) : null;

            el.Attributes.Add(new AxmlAttribute(aName, dataType, rawValue, value, str,
                FormatValue(pool, rawValue, dataType, value)));
        }
        return el;
    }

    private static string StrOf(List<string> pool, int index)
        => index >= 0 && index < pool.Count ? pool[index] : "";

    /// <summary>属性值 → 展示字符串（与原 ApkAnalysis.TypedValue 完全一致的格式）</summary>
    private static string FormatValue(List<string> pool, uint raw, byte type, uint data)
    {
        if (raw < (uint)pool.Count && pool[(int)raw].Length > 0) return pool[(int)raw];

        int i = unchecked((int)data);
        switch (type)
        {
            case 0x00: return "";
            case 0x01: return "@0x" + data.ToString("X8", CultureInfo.InvariantCulture);
            case 0x02: return "?" + data.ToString("X8", CultureInfo.InvariantCulture);
            case 0x03: return StrOf(pool, (int)data);
            case 0x04: return BitConverter.Int32BitsToSingle(i).ToString("0.###", CultureInfo.InvariantCulture);
            case 0x10: return i.ToString(CultureInfo.InvariantCulture);
            case 0x11: return "0x" + data.ToString("X", CultureInfo.InvariantCulture);
            case 0x12: return data != 0 ? "true" : "false";
            case >= 0x1c and <= 0x1f: return "#" + data.ToString("X8", CultureInfo.InvariantCulture);
            default: return "0x" + data.ToString("X", CultureInfo.InvariantCulture);
        }
    }

    // ---------------- 树上的小工具 ----------------

    internal static string Attr(AxmlElement? e, string name)
        => e == null ? "" : e.Attr(name);

    internal static AxmlElement? Find(AxmlElement? e, string name)
    {
        if (e == null) return null;
        if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
        foreach (var c in e.Children)
        {
            var hit = Find(c, name);
            if (hit != null) return hit;
        }
        return null;
    }

    internal static IEnumerable<AxmlElement> FindAll(AxmlElement? e, string name)
    {
        if (e == null) yield break;
        if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) yield return e;
        foreach (var c in e.Children)
            foreach (var hit in FindAll(c, name)) yield return hit;
    }

    /// <summary>深度优先遍历整棵树（含自身），带显式栈，不递归</summary>
    internal static IEnumerable<AxmlElement> Walk(AxmlElement root)
    {
        var stack = new Stack<AxmlElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            for (var i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
        }
    }

    // ---------------- 兼容旧调用点的单遍接口 ----------------

    /// <summary>列出 XML 里所有属性（元素名 + 属性名 + 类型 + 原始值 + 字符串值）,用于遍历 layer-list 等</summary>
    internal static List<(string elem, string attr, byte dataType, uint data, string? str)> AllAttrs(byte[]? axml)
    {
        var list = new List<(string, string, byte, uint, string?)>();
        var root = Parse(axml);
        if (root == null) return list;

        foreach (var el in Walk(root))
            foreach (var a in el.Attributes)
                list.Add((el.Name, a.Name, a.DataType, a.Data, a.StringValue));
        return list;
    }

    /// <summary>查找某元素的某属性（返回资源 ID 或字符串），找不到返回 (0, null)</summary>
    internal static (uint resId, string? str) FindAttr(byte[]? axml, string element, string attr)
    {
        var root = Parse(axml);
        if (root == null) return (0, null);

        foreach (var el in Walk(root))
        {
            if (!string.Equals(el.Name, element, StringComparison.Ordinal)) continue;
            foreach (var a in el.Attributes)
            {
                if (!string.Equals(a.Name, attr, StringComparison.Ordinal)) continue;
                if (a.DataType == 0x01) return (a.Data, null);
                if (a.DataType == 0x03) return (0, a.StringValue ?? "");
            }
        }
        return (0, null);
    }

    /// <summary>取 &lt;application&gt; 的 icon / roundIcon：返回 (资源ID, 直接路径)</summary>
    internal static (uint resId, string? directPath) ResolveIcon(byte[]? axml)
    {
        var root = Parse(axml);
        if (root == null) return (0, null);

        uint roundId = 0;
        string? roundPath = null;

        foreach (var app in FindAll(root, "application"))
        {
            uint iconId = 0;
            string? iconPath = null;

            foreach (var a in app.Attributes)
            {
                if (a.Name == "icon")
                {
                    if (a.DataType == 0x01) iconId = a.Data;
                    else if (a.DataType == 0x03) iconPath = a.StringValue;
                }
                else if (a.Name == "roundIcon")
                {
                    if (a.DataType == 0x01) roundId = a.Data;
                    else if (a.DataType == 0x03) roundPath = a.StringValue;
                }
            }

            if (iconId != 0 || iconPath != null) return (iconId, iconPath);
            if (roundId != 0 || roundPath != null) return (roundId, roundPath);
        }

        return (roundId, roundPath);
    }

    // ---------------- 序列化 ----------------

    internal static string ToXml(this AxmlElement root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        Write(sb, root, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, AxmlElement e, int depth)
    {
        var pad = new string(' ', depth * 2);
        var name = e.Name;
        if (string.Equals(name, "manifest", StringComparison.Ordinal))
            sb.Append(pad).Append("<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\"");
        else
            sb.Append(pad).Append("<").Append(name);

        foreach (var a in e.Attributes)
            sb.Append(" ").Append(a.Name).Append("=\"").Append(Escape(a.Value)).Append("\"");

        if (e.Children.Count == 0)
        {
            sb.AppendLine(" />");
            return;
        }

        sb.AppendLine(">");
        foreach (var c in e.Children) Write(sb, c, depth + 1);
        sb.Append(pad).AppendLine("</" + name + ">");
    }

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

/// <summary>AXML 元素（解析结果的唯一模型）</summary>
internal sealed class AxmlElement
{
    public string Name = "";
    public List<AxmlAttribute> Attributes = new();
    public List<AxmlElement> Children = new();

    /// <summary>按名取属性（忽略大小写，与原 Axml.Attr 语义一致）；没有返回空串</summary>
    public string Attr(string name)
    {
        foreach (var a in Attributes)
            if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a.Value;
        return "";
    }

    public AxmlAttribute? Find(string name)
    {
        foreach (var a in Attributes)
            if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }
}

/// <summary>AXML 属性：保留原始类型与值，同时给出展示字符串</summary>
internal readonly struct AxmlAttribute
{
    public readonly string Name;
    public readonly byte DataType;
    public readonly uint RawValue;      // "raw" 字段：指向字符串池的下标
    public readonly uint Data;          // 值本体（资源 ID / 字符串下标 / int / 颜色 …）
    public readonly string? StringValue; // dataType == 0x03 时的字符串，其他类型为 null
    public readonly string Value;        // 展示用（带 @0x / #RRGGBBAA 等前缀）

    public AxmlAttribute(string name, byte dataType, uint rawValue, uint data, string? stringValue, string value)
    {
        Name = name;
        DataType = dataType;
        RawValue = rawValue;
        Data = data;
        StringValue = stringValue;
        Value = value;
    }
}
