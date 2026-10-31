using System;
using System.Collections.Generic;
using System.Text;

namespace DashFile.Services;

/// <summary>
/// 极简 resources.arsc 解析器：把资源 ID（如 0x7f0e0002）解析成 APK 内的文件路径。
/// 只做“按 ID 找文件路径 + 选最合适的密度”这一件事，足够取应用图标。
/// </summary>
public static class ArscIconResolver
{
    private const ushort RES_STRING_POOL_TYPE = 0x0001;
    private const ushort RES_TABLE_TYPE = 0x0002;
    private const ushort RES_TABLE_PACKAGE_TYPE = 0x0200;
    private const ushort RES_TABLE_TYPE_TYPE = 0x0201;

    /// <summary>资源引用最多跟几层（正常图标链 1~2 层，超过就是畸形数据/环）</summary>
    private const int MaxRefDepth = 8;

    public sealed record Candidate(string Path, int Density, bool Night);

    /// <summary>解析出该资源 ID 的全部候选（不同密度/主题），按优先级排好序</summary>
    public static List<Candidate> Resolve(byte[] arsc, uint resId, int depth = 0)
    {
        var result = new List<Candidate>();
        if (arsc == null || arsc.Length < 12) return result;

        // 资源引用可能自指（@xxx 指回自己）或 A→B→A 成环。
        //   以前直接递归，构造出来的 arsc 会让 CLR 抛 StackOverflowException —— 不可捕获，进程直接消失。
        if (depth > MaxRefDepth) return result;

        var type = U16(arsc, 0);
        if (type != RES_TABLE_TYPE) return result;

        var headerSize = U16(arsc, 2);
        var packageCount = U32(arsc, 8);

        var globalStrings = new List<string>();
        var pkgId = (int)(resId >> 24);
        var typeId = (int)((resId >> 16) & 0xFF);
        var entryIdx = (int)(resId & 0xFFFF);

        int pos = headerSize;
        var pkgsSeen = 0;

        while (pos + 8 <= arsc.Length && pkgsSeen < packageCount)
        {
            var chunkType = U16(arsc, pos);
            var chunkHeader = U16(arsc, pos + 2);
            var chunkSize = (int)U32(arsc, pos + 4);
            if (chunkSize <= 0 || pos + chunkSize > arsc.Length) break;

            if (chunkType == RES_STRING_POOL_TYPE)
            {
                globalStrings = AxmlReader.ReadStringPool(arsc, pos);
            }
            else if (chunkType == RES_TABLE_PACKAGE_TYPE)
            {
                pkgsSeen++;
                var id = U32(arsc, pos + 8);
                if (id == (uint)pkgId)
                {
                    ReadPackage(arsc, pos, chunkHeader, chunkSize, typeId, entryIdx, globalStrings, result, depth);
                    break;
                }
            }

            pos += chunkSize;
        }

        // 高密度优先；夜间主题排后面
        result.Sort((a, b) =>
        {
            if (a.Night != b.Night) return a.Night ? 1 : -1;
            return b.Density.CompareTo(a.Density);
        });
        return result;
    }

    private static void ReadPackage(byte[] b, int pkgStart, int pkgHeader, int pkgSize,
        int typeId, int entryIdx, List<string> globalStrings, List<Candidate> outList, int depth = 0)
    {
        int pos = pkgStart + pkgHeader;
        var end = pkgStart + pkgSize;

        while (pos + 8 <= end && pos + 8 <= b.Length)
        {
            var chunkType = U16(b, pos);
            var headerSize = U16(b, pos + 2);
            var chunkSize = (int)U32(b, pos + 4);
            if (chunkSize <= 0 || pos + chunkSize > b.Length) break;

            if (chunkType == RES_TABLE_TYPE_TYPE)
            {
                var id = b[pos + 8];
                if (id == typeId)
                {
                    TryReadType(b, pos, headerSize, chunkSize, entryIdx, globalStrings, outList, depth);
                }
            }

            pos += chunkSize;
        }
    }

    private static void TryReadType(byte[] b, int start, int headerSize, int chunkSize,
        int entryIdx, List<string> globalStrings, List<Candidate> outList, int depth = 0)
    {
        var entryCount = (int)U32(b, start + 12);
        var entriesStart = (int)U32(b, start + 16);
        var typeFlags = b[start + 9];

        var configOff = start + 20;
        var density = (int)U16(b, configOff + 14);

        var offsetsPos = start + headerSize;
        var entryOff = -1;

        if ((typeFlags & 0x01) != 0)
        {
            // 稀疏表：entryCount 个 (idx u16, offset u16) 对
            for (var i = 0; i < entryCount; i++)
            {
                var p = offsetsPos + i * 4;
                if (p + 4 > b.Length) break;
                if (U16(b, p) != entryIdx) continue;
                entryOff = U16(b, p + 2) * 4;
                break;
            }
        }
        else if ((typeFlags & 0x02) != 0)
        {
            // 紧凑偏移（Android 12+）：u16 偏移，单位 4 字节
            var p = offsetsPos + entryIdx * 2;
            if (p + 2 > b.Length) return;
            var o16 = U16(b, p);
            if (o16 == 0xFFFF) return;
            entryOff = o16 * 4;
        }
        else
        {
            if (entryIdx >= entryCount) return;
            var p = offsetsPos + entryIdx * 4;
            if (p + 4 > b.Length) return;
            entryOff = (int)U32(b, p);
            if (entryOff == -1) return;
        }

        if (entryOff < 0) return;

        var entryPos = start + entriesStart + entryOff;
        // entriesStart 是 U32→int，可为负；负的 entryPos 会让后面的裸索引器抛异常
        if (entryPos < 0 || entryPos + 8 > b.Length) return;

        var flags = U16(b, entryPos + 2);
        var compact = (flags & 0x0008) != 0;   // 紧凑条目：value 紧跟 8 字节头

        var valuePos = compact ? entryPos + 8 : entryPos + U16(b, entryPos);
        if (valuePos + 8 > b.Length) return;

        var dataType = b[valuePos + 3];
        var data = U32(b, valuePos + 4);

        if (dataType == 0x01 && data != 0)      // 引用另一个资源，继续跟一层（深度受限）
        {
            if (depth >= MaxRefDepth) return;
            foreach (var c in Resolve(b, data, depth + 1)) outList.Add(c);
            return;
        }

        if (dataType != 0x03) return;
        if (data >= (uint)globalStrings.Count) return;

        var path = globalStrings[(int)data];
        // 普通 APK 是 res/...；ColorOS / 资源混淆后的 APK 会变成 r/...
        // 以前只认 res/，这些应用会得到 0 个候选，最后只能显示字母块。
        if (string.IsNullOrEmpty(path) || !IsResourcePath(path)) return;

        outList.Add(new Candidate(path, density, path.Contains("night", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsResourcePath(string path)
        => path.StartsWith("res/", StringComparison.Ordinal) ||
           path.StartsWith("r/", StringComparison.Ordinal);

    /// <summary>把资源 ID 解析成颜色值（矢量图里的 fillColor 可能是 @color/xxx）</summary>
    public static uint? ResolveColor(byte[] arsc, uint resId)
    {
        if (arsc == null || arsc.Length < 12) return null;
        if (U16(arsc, 0) != RES_TABLE_TYPE) return null;

        int pos = U16(arsc, 2);
        var globalStrings = new List<string>();
        var pkgId = (int)(resId >> 24);
        var typeId = (int)((resId >> 16) & 0xFF);
        var entryIdx = (int)(resId & 0xFFFF);

        while (pos + 8 <= arsc.Length)
        {
            var chunkType = U16(arsc, pos);
            var chunkHeader = U16(arsc, pos + 2);
            var chunkSize = (int)U32(arsc, pos + 4);
            if (chunkSize <= 0 || pos + chunkSize > arsc.Length) break;

            if (chunkType == RES_STRING_POOL_TYPE) globalStrings = AxmlReader.ReadStringPool(arsc, pos);
            else if (chunkType == RES_TABLE_PACKAGE_TYPE && U32(arsc, pos + 8) == (uint)pkgId)
            {
                var q = pos + chunkHeader;
                var end = pos + chunkSize;
                while (q + 8 <= end)
                {
                    var t = U16(arsc, q);
                    var hs = U16(arsc, q + 2);
                    var sz = (int)U32(arsc, q + 4);
                    if (sz <= 0 || q + sz > arsc.Length) break;

                    if (t == RES_TABLE_TYPE_TYPE && arsc[q + 8] == typeId)
                    {
                        var color = ReadColorFromType(arsc, q, hs, entryIdx);
                        if (color.HasValue) return color;
                    }
                    q += sz;
                }
            }

            pos += chunkSize;
        }
        return null;
    }

    /// <summary>把资源 ID 解析成字符串（应用名称就存在这里）</summary>
    public static string? ResolveString(byte[] arsc, uint resId)
    {
        if (arsc == null || arsc.Length < 12) return null;
        if (U16(arsc, 0) != RES_TABLE_TYPE) return null;

        int pos = U16(arsc, 2);
        var globalStrings = new List<string>();
        var pkgId = (int)(resId >> 24);
        var typeId = (int)((resId >> 16) & 0xFF);
        var entryIdx = (int)(resId & 0xFFFF);
        string? defaultStr = null;
        string? zhStr = null;

        while (pos + 8 <= arsc.Length)
        {
            var chunkType = U16(arsc, pos);
            var chunkHeader = U16(arsc, pos + 2);
            var chunkSize = (int)U32(arsc, pos + 4);
            if (chunkSize <= 0 || pos + chunkSize > arsc.Length) break;

            if (chunkType == RES_STRING_POOL_TYPE) globalStrings = AxmlReader.ReadStringPool(arsc, pos);
            else if (chunkType == RES_TABLE_PACKAGE_TYPE && U32(arsc, pos + 8) == (uint)pkgId)
            {
                var q = pos + chunkHeader;
                var end = pos + chunkSize;
                while (q + 8 <= end)
                {
                    var t = U16(arsc, q);
                    var hs = U16(arsc, q + 2);
                    var sz = (int)U32(arsc, q + 4);
                    if (sz <= 0 || q + sz > arsc.Length) break;
                    if (t == RES_TABLE_TYPE_TYPE && arsc[q + 8] == typeId)
                    {
                        var str = ReadStringFromType(arsc, q, hs, entryIdx, globalStrings);
                        if (str != null)
                        {
                            // ResTable_config: size(4) mcc(2) mnc(2) language(2) country(2)
                            // config 从 q+20 开始，所以 language 在 q+28
                            var langPos = q + 20 + 8;          // language[2]
                            var countryPos = langPos + 2;      // country[2]
                            var isZh = langPos + 2 < arsc.Length &&
                                       arsc[langPos] == (byte)'z' && arsc[langPos + 1] == (byte)'h';
                            var isCn = countryPos + 2 < arsc.Length &&
                                       arsc[countryPos] == (byte)'C' && arsc[countryPos + 1] == (byte)'N';

                            if (isZh && isCn) return str;      // 简体中文：最高优先级
                            if (isZh) zhStr ??= str;           // 繁体先备选
                            else defaultStr ??= str;
                        }
                    }
                    q += sz;
                }
            }
            pos += chunkSize;
        }
        return zhStr ?? defaultStr;
    }

    private static string? ReadStringFromType(byte[] b, int start, int headerSize, int entryIdx, List<string> pool)
    {
        var entryCount = (int)U32(b, start + 12);
        var entriesStart = (int)U32(b, start + 16);
        var typeFlags = b[start + 9];
        var offsetsPos = start + headerSize;
        var entryOff = -1;

        if ((typeFlags & 0x01) != 0)
        {
            for (var i = 0; i < entryCount; i++)
            {
                var p = offsetsPos + i * 4;
                if (p + 4 > b.Length) break;
                if (U16(b, p) != entryIdx) continue;
                entryOff = U16(b, p + 2) * 4;
                break;
            }
        }
        else if ((typeFlags & 0x02) != 0)
        {
            var p = offsetsPos + entryIdx * 2;
            if (p + 2 > b.Length) return null;
            var o16 = U16(b, p);
            if (o16 == 0xFFFF) return null;
            entryOff = o16 * 4;
        }
        else
        {
            if (entryIdx >= entryCount) return null;
            var p = offsetsPos + entryIdx * 4;
            if (p + 4 > b.Length) return null;
            entryOff = (int)U32(b, p);
            if (entryOff == -1) return null;
        }

        if (entryOff < 0) return null;
        var entryPos = start + entriesStart + entryOff;
        // entriesStart 是 U32→int，可为负；负的 entryPos 会让裸索引器抛 IndexOutOfRange
        if (entryPos < 0 || entryPos + 8 > b.Length) return null;

        var flags = U16(b, entryPos + 2);
        var compact = (flags & 0x0008) != 0;
        var valuePos = compact ? entryPos + 8 : entryPos + U16(b, entryPos);
        if (valuePos + 8 > b.Length) return null;

        var dataType = b[valuePos + 3];
        var data = U32(b, valuePos + 4);
        if (dataType != 0x03 || data >= (uint)pool.Count) return null;

        var s = pool[(int)data];
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static uint? ReadColorFromType(byte[] b, int start, int headerSize, int entryIdx)
    {
        var entryCount = (int)U32(b, start + 12);
        var entriesStart = (int)U32(b, start + 16);
        var typeFlags = b[start + 9];
        var offsetsPos = start + headerSize;
        var entryOff = -1;

        if ((typeFlags & 0x01) != 0)
        {
            for (var i = 0; i < entryCount; i++)
            {
                var p = offsetsPos + i * 4;
                if (p + 4 > b.Length) break;
                if (U16(b, p) != entryIdx) continue;
                entryOff = U16(b, p + 2) * 4;
                break;
            }
        }
        else if ((typeFlags & 0x02) != 0)
        {
            var p = offsetsPos + entryIdx * 2;
            if (p + 2 > b.Length) return null;
            var o16 = U16(b, p);
            if (o16 == 0xFFFF) return null;
            entryOff = o16 * 4;
        }
        else
        {
            if (entryIdx >= entryCount) return null;
            var p = offsetsPos + entryIdx * 4;
            if (p + 4 > b.Length) return null;
            entryOff = (int)U32(b, p);
            if (entryOff == -1) return null;
        }

        if (entryOff < 0) return null;
        var entryPos = start + entriesStart + entryOff;
        // entriesStart 是 U32→int，可为负；负的 entryPos 会让裸索引器抛 IndexOutOfRange
        if (entryPos < 0 || entryPos + 8 > b.Length) return null;

        var flags = U16(b, entryPos + 2);
        var compact = (flags & 0x0008) != 0;
        var valuePos = compact ? entryPos + 8 : entryPos + U16(b, entryPos);
        if (valuePos + 8 > b.Length) return null;

        var dataType = b[valuePos + 3];
        var data = U32(b, valuePos + 4);

        return dataType switch
        {
            0x1c or 0x1d or 0x1e or 0x1f => data,      // 直接是颜色
            0x01 when data != 0 => null,                 // 引用别的颜色（不追）
            _ => null
        };
    }

    // ---------- 字符串池 ----------

    // 字符串池只有一份实现（AxmlReader.ReadStringPool）：AXML / resources.arsc 用的是同一套格式，
    // 以前这里还有第四份拷贝，解码细节（双长度前缀）各写各的。

    // 二进制读取统一走 AxmlReader（带边界检查）：以前这里是裸索引器，
    // 任何一处下标算错就是 IndexOutOfRangeException，表现为"图标解析失败"。
    private static ushort U16(byte[] b, int o) => AxmlReader.U16(b, o);
    private static uint U32(byte[] b, int o) => AxmlReader.U32(b, o);
}