using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services;

/// <summary>
/// 直接读写 KernelSU 系的授权表（/data/adb/ksu/.allowlist），完全不经过管理器 App。
///
/// 授权表 = 文件头 + N 条定长记录。不同 fork 的记录长度不一样，实测有两种：
///   老版：头 12 字节，记录 780 字节，记录内包名从 0 开始；
///   新版（KernelSU-Next / SukiSU 3.x）：头 8 字节，记录 784 字节，包名前多 4 字节（版本/标志）。
/// 但两种记录「包名之后」的字段偏移一致：
///   包名 256 字节 | uid 4 | allow_su 4 | …（root profile）… | SELinux 上下文 @700 | flags @772
/// 所以这里不写死布局：读的时候按候选布局逐条校验（包名可读 + uid 合理），
/// 写回时原样保留不认识的字节，只改 allow 字段；新增记录则沿用同表记录的前缀。
/// </summary>
public sealed class SuperuserService
{
    // 不同 KSU fork 的授权表文件名基本一致，但少数版本/移植会用不带点的 allowlist；
    // 读的时候依次尝试，写回时写回“实际读到的那个路径”，不要凭空创建新文件。
    private static readonly string[] CandidatePaths =
    {
        DevicePaths.KsuAllowlist,
        DevicePaths.KsuAllowlistAlt,
    };

    private const int PackageLen = 256;
    private const int UidOffset = 256;
    private const int AllowOffset = 260;
    private const int ProfileFieldOffset = 268;
    private const int SeContextOffset = 700;
    private const int FlagsOffset = 772;
    private const int BaseRecordLen = 780;   // 包名字段之后的相对长度（含包名本身）
    private const int ExtendedRecordLen = 784;  // 新版：多 4 字节 profile 前缀
    private const int ExtendedPrefixLen = 4;

    private readonly RemoteFileSystem _fs;
    private readonly string _variant;
    private string? _path;

    /// <summary>读-改-写必须串行：连点两个开关时，后一个不能基于旧快照重排整表</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SuperuserService(RemoteFileSystem fs, string? variant = null)
    {
        _fs = fs;
        _variant = variant ?? "";
    }

    /// <summary>本次识别时是否存在多个"都解析得通"的布局（只有表里恰好 1 条记录时会出现）</summary>
    public bool LayoutAmbiguous { get; private set; }

    /// <summary>歧义时的统一提示：不猜布局，让表自己变长再判定</summary>
    internal const string AmbiguousAppendHint =
        "授权表只有 1 条记录，无法判定它用 780 还是 784 的记录步长，新增/删除会写坏整表，已中止（原有授权未改动）。" +
        "请在 KernelSU 管理器里再授权一个应用，表变长后即可自动判定；也可以直接用管理器添加。";

    private string AllowlistPath => _path ?? CandidatePaths[0];

    public sealed record Entry(string Package, int Uid, bool Allowed, byte[] Raw);

    /// <summary>表头 + 记录布局（读成功后缓存，写回时复用）。</summary>
    internal sealed record Layout(int HeaderLen, int RecordLen, int PackageOffset, int Count, byte[] Header);

    private Layout? _layout;

    /// <summary>最近一次读取失败的原因（成功时为 null），用于界面/日志提示。</summary>
    public string? LastError { get; private set; }

    /// <summary>文件存在但格式不认识（只有这种情况才值得降级为只读）。</summary>
    public bool FormatUnsupported { get; private set; }

    // 候选布局：头长 / 记录长 / 包名在记录内的偏移。首个校验通过的即采用。
    private static readonly int[] HeaderLens = { 12, 8, 4 };
    private static readonly int[] RecordLens = { 780, 784, 776, 788, 792 };
    private static readonly int[] PackageOffsets = { 0, 4, 8 };

    /// <summary>读取当前授权表；文件不存在/格式不符返回 null（调用方应降级为只读）。</summary>
    public async Task<List<Entry>?> ReadAsync(CancellationToken ct = default)
    {
        LastError = null;
        FormatUnsupported = false;

        foreach (var candidate in CandidatePaths)
        {
            byte[] bytes;
            try
            {
                bytes = await _fs.ReadBytesAsync(candidate, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = $"读取 {candidate} 失败：{ex.Message}";
                continue;
            }

            if (bytes.Length == 0) continue;   // 没有这个文件，试下一个

            // 1~2 字节的畸形响应：以前直接读 bytes[1]/bytes[2] 抛 IndexOutOfRange
            if (bytes.Length < 3)
            {
                FormatUnsupported = true;
                LastError = $"{candidate} 内容异常（只有 {bytes.Length} 字节）";
                continue;
            }

            if (bytes[0] != (byte)'U' || bytes[1] != (byte)'S' || bytes[2] != (byte)'K')
            {
                FormatUnsupported = true;
                LastError = $"{candidate} 不是 USK 授权表（{bytes.Length} 字节）";
                continue;
            }

            var layout = DetectLayout(bytes);
            if (layout == null)
            {
                FormatUnsupported = true;
                LastError = $"授权表格式无法识别（{bytes.Length} 字节，可能是未适配的 fork）";
                continue;
            }

            var list = new List<Entry>(layout.Count);
            for (var i = 0; i < layout.Count; i++)
            {
                var recordStart = layout.HeaderLen + i * layout.RecordLen;
                var raw = new byte[layout.RecordLen];
                Array.Copy(bytes, recordStart, raw, 0, layout.RecordLen);

                var pkg = ReadCString(raw, layout.PackageOffset, PackageLen);
                var uid = BitConverter.ToInt32(raw, layout.PackageOffset + UidOffset);
                var allow = BitConverter.ToInt32(raw, layout.PackageOffset + AllowOffset) != 0;
                if (pkg.Length == 0) continue;
                list.Add(new Entry(pkg, uid, allow, raw));
            }

            _layout = layout;
            _path = candidate;
            return list;
        }

        return null;
    }

    /// <summary>设置某个包的授权状态；不存在则追加</summary>
    public async Task<(bool Ok, string Message)> SetAllowedAsync(string pkg, int uid, bool allowed,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            if (entries == null || _layout == null) return (false, "授权表格式不支持，未做任何修改");

            var idx = entries.FindIndex(e => e.Package == pkg);
            if (idx >= 0)
            {
                // 只改 allow 这 4 字节：两种布局下 allow 的**绝对偏移完全一致**（12+260 == 8+4+260），
                // 所以即使布局有歧义，原地打补丁也是零风险的。
                var raw = (byte[])entries[idx].Raw.Clone();
                BitConverter.GetBytes(allowed ? 1 : 0)
                    .CopyTo(raw, _layout.PackageOffset + AllowOffset);
                entries[idx] = entries[idx] with { Allowed = allowed, Raw = raw };
            }
            else
            {
                // 新增要按记录步长重排整表，这时布局必须是真的。
                // 只有 1 条记录的授权表两种布局同长同偏移，无法判定 —— 绝不猜，宁可让用户多点一步。
                if (LayoutAmbiguous) return (false, AmbiguousAppendHint);

                entries.Add(new Entry(pkg, uid, allowed, BuildRecord(pkg, uid, allowed, entries)));
            }

            return await WriteAsync(entries, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>从表中移除一个包</summary>
    public async Task<(bool Ok, string Message)> RemoveAsync(string pkg, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            if (entries == null || _layout == null) return (false, "授权表格式不支持，未做任何修改");

            var idx = entries.FindIndex(e => e.Package == pkg);
            if (idx < 0) return (true, "该应用不在授权表中");

            // 删除会改变记录条数 → 必须按正确步长重排，歧义时同样不能猜
            if (LayoutAmbiguous) return (false, AmbiguousAppendHint);

            entries.RemoveAt(idx);
            return await WriteAsync(entries, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // ---------------- 布局识别 ----------------

    private Layout? DetectLayout(byte[] bytes)
    {
        var candidates = new List<Layout>();

        foreach (var headerLen in HeaderLens)
        {
            if (bytes.Length <= headerLen) continue;
            var body = bytes.Length - headerLen;

            foreach (var recordLen in RecordLens)
            {
                if (body % recordLen != 0) continue;
                var count = body / recordLen;
                if (count <= 0) continue;

                foreach (var packageOffset in PackageOffsets)
                {
                    if (packageOffset + FlagsOffset + 8 > recordLen) continue;
                    if (!Validate(bytes, headerLen, recordLen, packageOffset, count)) continue;
                    if (candidates.Exists(c => c.HeaderLen == headerLen && c.RecordLen == recordLen &&
                                               c.PackageOffset == packageOffset)) continue;

                    var header = new byte[headerLen];
                    Array.Copy(bytes, header, headerLen);
                    candidates.Add(new Layout(headerLen, recordLen, packageOffset, count, header));
                }
            }
        }

        LayoutAmbiguous = candidates.Count > 1;
        if (candidates.Count > 0) return PickLayout(candidates, _variant);

        // 空表：只有一个文件头（老版 12 字节 / 新版 8 字节 / 只有魔数 4 字节）
        if (bytes.Length is 4 or 8 or 12)
        {
            var extended = PrefersExtendedLayout(_variant);
            // header 数组长度必须等于 Layout.HeaderLen：以前 4 字节文件 + 新版枚举会造出
            //   Layout(8,…) 却带 12 字节 header，写回时 CopyTo 直接越界。
            var headerLen = extended ? 8 : 12;
            var header = new byte[headerLen];
            Array.Copy(bytes, header, Math.Min(headerLen, bytes.Length));
            return extended
                ? new Layout(8, ExtendedRecordLen, ExtendedPrefixLen, 0, header)
                : new Layout(headerLen, BaseRecordLen, 0, 0, header);
        }

        return null;
    }

    /// <summary>
    /// 多个布局都解析得通时怎么选。
    ///
    /// 这里为什么需要"猜"：新版（头 8 + 记录 784，记录前有 4 字节前缀）在**只有 1 条授权**时
    ///   总长 792，和老版（头 12 + 记录 780）完全同长；而且包名/uid/allow/flags 的**绝对偏移也相同**
    ///   （12+260 == 8+4+260，12+772 == 8+4+772），所以单条记录的数据本身无法区分两种布局。
    ///   以前固定按 {12,8,4} 的顺序先命中老版，于是新版单条表被当成 780 步长：读没问题，
    ///   一旦新增第 2 条就按 12+2×780 重排，(1572-8)%784≠0 → 整表不可解析，授权全丢。
    ///   现在用"管理器 fork 名"这个外部证据来选：3.x 那几家用 784，其余按老版。
    /// </summary>
    private static Layout PickLayout(List<Layout> candidates, string variant)
    {
        if (candidates.Count == 1) return candidates[0];

        var extended = PrefersExtendedLayout(variant);

        // 只在"真实存在的两种布局"里挑。别的组合（例如 4+788）只是长度整除+包名恰好可读的
        // 巧合解，跟着它重排会把表写坏。
        var known = candidates.Where(c =>
            (c.HeaderLen == 8 && c.RecordLen == ExtendedRecordLen && c.PackageOffset == ExtendedPrefixLen) ||
            (c.HeaderLen == 12 && c.RecordLen == BaseRecordLen && c.PackageOffset == 0)).ToList();

        var pool = known.Count > 0 ? known : candidates;
        return pool
            .OrderByDescending(c => IsExtended(c) == extended ? 1 : 0)
            .ThenBy(c => c.HeaderLen)
            .ThenBy(c => c.RecordLen)
            .First();
    }

    /// <summary>新版布局特征：记录里包名前面还有 4 字节前缀</summary>
    private static bool IsExtended(Layout c) => c.PackageOffset > 0;

    /// <summary>从管理器名推断布局：这几家 3.x 系用 头8 + 记录784（前 4 字节 profile 版本）</summary>
    private static bool PrefersExtendedLayout(string variant)
        => variant.Contains("Next", StringComparison.OrdinalIgnoreCase)
           || variant.Contains("SukiSU", StringComparison.OrdinalIgnoreCase)
           || variant.Contains("ReSukiSU", StringComparison.OrdinalIgnoreCase)
           || variant.Contains("KowSU", StringComparison.OrdinalIgnoreCase)
           || variant.Contains("MKSU", StringComparison.OrdinalIgnoreCase);

    /// <summary>逐条校验：包名可读、uid 在 Android 应用 uid 范围内。错一个就整体否决，避免误判布局。</summary>
    private static bool Validate(byte[] bytes, int headerLen, int recordLen, int packageOffset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var start = headerLen + i * recordLen + packageOffset;
            if (!IsPackageName(bytes, start)) return false;
            if (start + UidOffset + 4 > bytes.Length) return false;
            var uid = BitConverter.ToInt32(bytes, start + UidOffset);
            if (uid < 1000 || uid > 999999) return false;
        }
        return true;
    }

    private static bool IsPackageName(byte[] bytes, int offset)
    {
        if (offset >= bytes.Length) return false;
        var first = bytes[offset];
        if (!(first is >= (byte)'a' and <= (byte)'z' || first is >= (byte)'A' and <= (byte)'Z')) return false;

        var len = 0;
        for (var i = 0; i < PackageLen; i++)
        {
            if (offset + i >= bytes.Length) return false;
            var b = bytes[offset + i];
            if (b == 0) break;
            var ok = b is >= (byte)'a' and <= (byte)'z'
                     || b is >= (byte)'A' and <= (byte)'Z'
                     || b is >= (byte)'0' and <= (byte)'9'
                     || b == (byte)'.' || b == (byte)'_' || b == (byte)'-';
            if (!ok) return false;
            len++;
        }
        return len >= 3;
    }

    private static string ReadCString(byte[] buf, int offset, int max)
    {
        var end = Array.IndexOf(buf, (byte)0, offset, Math.Min(max, buf.Length - offset));
        if (end < 0) end = Math.Min(offset + max, buf.Length);
        return Encoding.UTF8.GetString(buf, offset, end - offset);
    }

    // ---------------- 写回 ----------------

    private byte[] BuildRecord(string pkg, int uid, bool allowed, List<Entry> existing)
    {
        var layout = _layout!;
        var raw = new byte[layout.RecordLen];
        var p = layout.PackageOffset;

        // 新版记录前 4 字节含义未知（所有记录都相同，疑似 profile 版本）：优先照抄同表记录，其次抄表头末尾
        if (p > 0)
        {
            if (existing.Count > 0) Array.Copy(existing[0].Raw, 0, raw, 0, p);
            else if (layout.Header.Length >= p) Array.Copy(layout.Header, layout.Header.Length - p, raw, 0, p);
        }

        var key = Encoding.UTF8.GetBytes(pkg);
        Array.Copy(key, 0, raw, p, Math.Min(key.Length, PackageLen - 1));
        BitConverter.GetBytes(uid).CopyTo(raw, p + UidOffset);
        BitConverter.GetBytes(allowed ? 1 : 0).CopyTo(raw, p + AllowOffset);
        BitConverter.GetBytes(1).CopyTo(raw, p + ProfileFieldOffset);   // 与现存记录一致

        var ctx = Encoding.UTF8.GetBytes("u:r:ksu:s0");
        Array.Copy(ctx, 0, raw, p + SeContextOffset, ctx.Length);
        BitConverter.GetBytes(1L).CopyTo(raw, p + FlagsOffset);        // flags
        return raw;
    }

    /// <summary>
    /// 按布局把记录拼成整表字节。抽成纯函数的原因：
    /// 布局识别的风险全在"重排之后还能不能被设备解析"，这条只能在字节层面验证。
    /// </summary>
    internal static byte[]? ComposeTable(Layout layout, IReadOnlyList<Entry> entries)
    {
        if (layout.Header.Length != layout.HeaderLen) return null;   // 头部长度自相矛盾

        var buf = new byte[layout.HeaderLen + entries.Count * layout.RecordLen];
        layout.Header.CopyTo(buf, 0);

        var off = layout.HeaderLen;
        foreach (var e in entries)
        {
            if (e.Raw.Length != layout.RecordLen) return null;
            Array.Copy(e.Raw, 0, buf, off, layout.RecordLen);
            off += layout.RecordLen;
        }
        return buf;
    }

    private async Task<(bool Ok, string Message)> WriteAsync(List<Entry> entries, CancellationToken ct)
    {
        var layout = _layout;
        if (layout == null) return (false, "读取表头失败");

        var buf = ComposeTable(layout, entries);
        if (buf == null) return (false, "内部记录长度不一致，已中止写入");
        var total = buf.Length;

        // 备份存到电脑（设备上不留任何文件）
        var backupDir = AppPaths.Su;
        Directory.CreateDirectory(backupDir);
        try
        {
            var old = await _fs.ReadBytesAsync(AllowlistPath, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(backupDir, "allowlist-" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".bak"),
                old, ct).ConfigureAwait(false);
        }
        catch { }

        if (!await _fs.WriteBytesAsync(AllowlistPath, buf, ct).ConfigureAwait(false))
            return (false, "写入失败（请检查 root 授权）");

        // 回读校验：只比长度等于没校验（改既有记录时长度必然不变），必须逐字节比对
        var check = await _fs.ReadBytesAsync(AllowlistPath, ct).ConfigureAwait(false);
        if (check.Length != total || !check.SequenceEqual(buf))
            return (false, $"写入后内容与预期不一致（{check.Length}/{total} 字节），原表已备份到电脑");

        return (true, "已写入，重启后生效");
    }
}
