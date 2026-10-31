using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SkiaSharp;
using DashFile.Models;

namespace DashFile.Services;

/// <summary>
/// 应用列表：包名 / 版本 / 图标。
/// 全程只读：只 list / 读取 APK 内容，绝不在设备上创建任何文件；
/// 图标缓存只写本机 %LOCALAPPDATA%\DashFile\icons。
/// </summary>
public sealed class AppService
{
    private readonly RemoteFileSystem _fs;
    private readonly SemaphoreSlim _unzipGate = new(1, 1);
    private string? _unzipExe;        // 已解析出的解包程序路径
    private bool _unzipIsBusybox;

    private static readonly string CacheDir = AppPaths.Icons;

    public AppService(RemoteFileSystem fs) => _fs = fs;

    private static bool DebugIcons => AppEnvironment.IconDebug;

    private static void IconDbg(string msg)
    {
        if (!DebugIcons) return;
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Log");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "icon-debug.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }

    // ---------- 解包程序探测 ----------

    /// <summary>上次探测只是"PATH 兜底"，没找到真程序：root 就绪后要允许再探一次</summary>
    private bool _unzipFallback;

    private async Task EnsureUnzipAsync(CancellationToken ct)
    {
        // 以前只要 _unzipExe 非空就永久返回：第一次取图标发生在 su 授权之前时，
        //   探测必然失败并缓存 "unzip"，之后整场会话都取不到图标（设备上没有 unzip 这个名字）。
        if (_unzipExe != null && !(_unzipFallback && _fs.UseRoot)) return;

        // 370 个应用会在并发任务里同时调到这里；不加锁会重复探测 unzip（每个 80ms）。
        await _unzipGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_unzipExe != null) return;

            // 只读探测：系统 unzip 优先，其次各 Root 实现的 busybox
            foreach (var candidate in new[]
                     {
                         DevicePaths.SystemBin + "/unzip",
                         DevicePaths.KsuBusybox,
                         DevicePaths.MagiskBusybox,
                         DevicePaths.ApdBusybox,
                     })
            {
                var r = await _fs.RunCommandAsync($"if [ -x {Sh.Q(candidate)} ]; then echo YES; fi", false, ct)
                                  .ConfigureAwait(false);
                if (r.Contains("YES"))
                {
                    _unzipExe = candidate;
                    _unzipIsBusybox = candidate.EndsWith("busybox", StringComparison.Ordinal);
                    _unzipFallback = false;
                    return;
                }
            }

            // 兜底：交给 PATH（标记为 fallback，root 就绪后还会重试真程序）
            if (_unzipExe == null)
            {
                _unzipExe = "unzip";
                _unzipFallback = true;
            }
        }
        finally
        {
            _unzipGate.Release();
        }
    }

    private string UnzipCmd(string rest) =>
        _unzipIsBusybox ? $"{_unzipExe} unzip {rest}" : $"{_unzipExe} {rest}";

    // ---------- 列表 ----------

    /// <summary>列出全部应用（用户+系统），超级用户页用</summary>
    public Task<List<AppItem>> ListAllAsync(CancellationToken ct = default) => ListAsync(false, ct, true);

    public async Task<List<AppItem>> ListAsync(bool system, CancellationToken ct = default, bool all = false)
    {
        var flag = all ? "" : (system ? "-s" : "-3");

        // 两段式的第一段：一次调用就把包名、APK 路径、versionCode 全拿回来。
        //   versionCode 不是给人看的（我们显示的是 versionName），但能让列表"立刻有版本号"，
        //   不用等每个应用各跑一次 dumpsys（那才是慢的根源：每个 100ms 左右）。
        var raw = await _fs.RunCommandAsync($"pm list packages {flag} -f --show-versioncode 2>/dev/null", false, ct)
                           .ConfigureAwait(false);

        // 老系统不支持 --show-versioncode 时，退回不带版本号的写法（第二段照样会补上）
        if (!raw.Contains("versionCode:", StringComparison.OrdinalIgnoreCase))
            raw = await _fs.RunCommandAsync($"pm list packages {flag} -f 2>/dev/null", false, ct)
                               .ConfigureAwait(false);

        var list = new List<AppItem>();
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("package:", StringComparison.Ordinal)) continue;

            var body = t[8..].Trim();          // /path/base.apk=com.foo versionCode:123

            var code = "";
            var mc = Regex.Match(body, @"\s+versionCode:(\d+)\s*$");
            if (mc.Success)
            {
                code = mc.Groups[1].Value;
                body = body[..mc.Index].Trim();
            }

            var eq = body.LastIndexOf('=');
            if (eq <= 0) continue;

            var apk = body[..eq].Trim();
            var pkg = body[(eq + 1)..].Trim();
            if (pkg.Length == 0) continue;

            list.Add(new AppItem
            {
                Package = pkg,
                IsSystem = system,
                ApkPath = apk,
                Version = code.Length > 0 ? "v" + code : ""      // 先给个占位版本，第二段会换成 versionName
            });
        }
        return list.OrderBy(x => x.Package, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>读取应用详情（全部来自 dumpsys package，只读）</summary>
    public async Task<List<(string Label, string Value)>> GetDetailsAsync(string pkg, string apk,
        CancellationToken ct = default)
    {
        var rows = new List<(string, string)>();
        string dump = "";
        try
        {
            dump = await _fs.RunCommandAsync($"dumpsys package {Sh.Q(pkg)}", false, ct).ConfigureAwait(false);
        }
        catch { }

        string Get(string pattern, string def = "—")
        {
            var m = Regex.Match(dump, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : def;
        }

        var versionName = Get(@"versionName=(\S+)");
        var versionCode = Get(@"versionCode=(\d+)");
        var minSdk = Get(@"minSdk=(\d+)");
        var targetSdk = Get(@"targetSdk=(\d+)");
        var uid = Get(@"userId=(\d+)", "");
        if (string.IsNullOrEmpty(uid))
        {
            try
            {
                var st = await _fs.RunCommandAsync(
                    $"stat -c %u {Sh.Q(DevicePaths.UserData(pkg))} 2>/dev/null", false, ct)
                    .ConfigureAwait(false);
                var mu = Regex.Match(st, @"(?m)^\s*(\d+)\s*$");
                if (mu.Success) uid = mu.Groups[1].Value;
            }
            catch { }
        }
        var dataDir = Get(@"dataDir=(\S+)");
        var codePath = Get(@"codePath=(\S+)");
        var firstInstall = Get(@"firstInstallTime=(.+)");
        var lastUpdate = Get(@"lastUpdateTime=(.+)");

        // 安装包大小
        var sizeText = "—";
        try
        {
            var stat = await _fs.RunCommandAsync($"stat -c %s {Sh.Q(apk)} 2>/dev/null", false, ct).ConfigureAwait(false);
            var mm = Regex.Match(stat, @"(?m)^\s*(\d+)\s*$");
            if (mm.Success) sizeText = FormatBytes(long.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        catch { }

        // 签名（dumpsys 里的 signatures 是一串哈希）
        var sig = "—";
        var sigMatch = Regex.Match(dump, @"signatures=PackageSignatures\{[^\[]*\[([0-9a-fA-F]+)\]");
        if (sigMatch.Success) sig = "已签名（" + sigMatch.Groups[1].Value[..Math.Min(16, sigMatch.Groups[1].Value.Length)] + "…）";
        else if (dump.Contains("signatures=null")) sig = "未签名";

        // 加固检测：常见加固厂商的 so / dex
        var packed = "未检测到加固";
        try
        {
            var listing = await _fs.RunCommandAsync(
                UnzipCmd($"-l {Sh.Q(apk)}") + " 2>/dev/null | grep -iE 'libjiagu|libshell|libDexHelper|libmobisec|libtup|libnesec|libexec.so|libprotect' | head -3",
                false, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(listing) && !listing.Contains("[退出码]", StringComparison.Ordinal))
            {
                var line = listing.Split('\n').FirstOrDefault(l => l.Contains("/"));
                if (line != null) packed = "疑似加固：" + line.Trim().Split(' ').Last();
            }
        }
        catch { }

        rows.Add(("包名", pkg));
        rows.Add(("版本", string.IsNullOrEmpty(versionCode) ? versionName : $"{versionName}（{versionCode}）"));
        rows.Add(("安装包大小", sizeText));
        rows.Add(("签名状态", sig));
        rows.Add(("加固状态", packed));
        rows.Add(("目标系统", string.IsNullOrEmpty(targetSdk) ? "—" : $"API {targetSdk}"));
        rows.Add(("最低系统", string.IsNullOrEmpty(minSdk) ? "—" : $"API {minSdk}"));
        rows.Add(("数据目录 1", string.IsNullOrEmpty(dataDir) ? "—" : dataDir));
        rows.Add(("数据目录 2", string.IsNullOrEmpty(dataDir)
            ? "—"
            : dataDir.Replace(DevicePaths.UserDataRoot + "/", DevicePaths.UserDeDataRoot + "/")));
        rows.Add(("APK 路径", string.IsNullOrEmpty(apk) ? "—" : apk));
        rows.Add(("首次安装", string.IsNullOrEmpty(firstInstall) ? "—" : firstInstall));
        rows.Add(("最后更新", string.IsNullOrEmpty(lastUpdate) ? "—" : lastUpdate));
        rows.Add(("UID", string.IsNullOrEmpty(uid) ? "—" : uid));

        _ = codePath;
        return rows;
    }

    private static string FormatBytes(long bytes)
    {
        string[] unit = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < unit.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {unit[i]}";
    }

    // 并发任务会同时读写标签缓存；Dictionary 并发写会损坏内部结构，改用 ConcurrentDictionary。
    private static readonly ConcurrentDictionary<string, string> LabelCache = new(StringComparer.Ordinal);
    private static readonly object LabelSaveLock = new();
    private static int _labelsLoaded;
    private static int _dirtyLabels;

    private static string LabelFile => AppPaths.Labels;

    private static void LoadLabels()
    {
        if (Interlocked.Exchange(ref _labelsLoaded, 1) == 1) return;
        try
        {
            if (!File.Exists(LabelFile)) return;
            var json = File.ReadAllText(LabelFile);
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (map != null)
                foreach (var kv in map) LabelCache[kv.Key] = kv.Value;
        }
        catch { }
    }

    /// <summary>
    /// 以前每解析出一个新名称就重写一次 labels.json（370 个应用 = 370 次全量写文件）。
    /// 现在每 20 个新名称写一次，退出时由 FlushLabels 兜底。
    /// </summary>
    private static void SaveLabels()
    {
        lock (LabelSaveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LabelFile)!);
                var snapshot = LabelCache.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                AtomicFile.WriteAllText(LabelFile, System.Text.Json.JsonSerializer.Serialize(snapshot));
            }
            catch (Exception ex) { AppLog.Add("写标签缓存失败：" + ex.Message); }
        }
    }

    /// <summary>退出时调用，把不足 20 个的增量也落盘。</summary>
    public static void FlushLabels()
    {
        if (Interlocked.Exchange(ref _dirtyLabels, 0) > 0) SaveLabels();
    }

    /// <summary>解析应用显示名称（android:label → resources.arsc 字符串）</summary>
    public async Task<string> GetLabelAsync(string pkg, string apk, CancellationToken ct = default)
    {
        LoadLabels();
        if (LabelCache.TryGetValue(pkg, out var cached) && !string.IsNullOrWhiteSpace(cached)) return cached;

        await EnsureUnzipAsync(ct).ConfigureAwait(false);   // 必须先探到解包程序

        string? label = null;
        try
        {
            var manifest = await ReadEntryAsync(apk, "AndroidManifest.xml", ct).ConfigureAwait(false);
            if (manifest.Length > 8)
            {
                var (id, lit) = AxmlManifestResolver.FindAttr(manifest, "application", "label");
                if (!string.IsNullOrWhiteSpace(lit)) label = lit;
                else if (id != 0)
                {
                    var arsc = await ReadEntryAsync(apk, "resources.arsc", ct).ConfigureAwait(false);
                    label = ArscIconResolver.ResolveString(arsc, id);
                }
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(label))
        {
            var parts = pkg.Split('.');
            label = parts.Length > 0 ? parts[^1] : pkg;
        }

        LabelCache[pkg] = label!;
        // 每 20 个新增写一次；剩下的由 FlushLabels 在退出时落盘
        if (Interlocked.Increment(ref _dirtyLabels) >= 20)
        {
            Interlocked.Exchange(ref _dirtyLabels, 0);
            SaveLabels();
        }
        return label!;
    }

    public async Task<string> GetVersionAsync(string pkg, CancellationToken ct = default)
    {
        try
        {
            var raw = await _fs.RunCommandAsync(
                $"dumpsys package {Sh.Q(pkg)} 2>/dev/null | grep -m1 versionName", false, ct).ConfigureAwait(false);
            var m = Regex.Match(raw, @"versionName=(\S+)");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 一次 dumpsys package 拿全部 versionName。
    /// 以前 370 个系统应用 = 370 次 adb 往返（约 30 秒），现在固定 1 次；
    /// 解析失败或包很少时调用方仍可退回 GetVersionAsync。
    /// </summary>
    public async Task<Dictionary<string, string>> GetVersionsAsync(
        IReadOnlyCollection<string> packages, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (packages == null || packages.Count == 0) return result;

        try
        {
            // 只回传 Package 头和 versionName 行，避免把几 MB 的 dumpsys 全量拉回电脑
            var raw = await _fs.RunCommandAsync(
                "dumpsys package 2>/dev/null | grep -E 'Package \\[|versionName='", false, ct)
                .ConfigureAwait(false);
            if (raw.Length < 32) return result;

            string? current = null;
            foreach (var rawLine in raw.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Package [", StringComparison.Ordinal))
                {
                    var end = line.IndexOf(']', "Package [".Length);
                    current = end > 0 ? line["Package [".Length..end] : null;
                    continue;
                }

                if (current == null || !line.StartsWith("versionName=", StringComparison.Ordinal)) continue;
                if (result.ContainsKey(current)) continue;

                var value = line["versionName=".Length..].Trim();
                if (value.Length > 0) result[current] = value;
            }
        }
        catch { }

        return result;
    }

    // ---------- 精确取图标 ----------

    /// <summary>只读地取出 APK 内某个条目的原始字节</summary>
    private async Task<byte[]> ReadEntryAsync(string apk, string entry, CancellationToken ct)
    {
        var args = _unzipIsBusybox
            ? new List<string> { "unzip", "-p", apk, entry }
            : new List<string> { "-p", apk, entry };

        var bytes = await _fs.ExecCaptureArgsAsync(_unzipExe!, args, false, ct).ConfigureAwait(false);
        if (bytes.Length < 8 && _fs.UseRoot)
        {
            IconDbg($"  ReadEntry({entry}) 普通权限失败({bytes.Length}B)，试 root");
            bytes = await _fs.ExecCaptureArgsAsync(_unzipExe!, args, true, ct).ConfigureAwait(false);
        }
        IconDbg($"  ReadEntry({entry}) => {bytes.Length}B head={Convert.ToHexString(bytes.AsSpan(0, Math.Min(8, bytes.Length)))}");
        return bytes;
    }

    /// <summary>
    /// 如果解析出来的是"自适应图标 XML"，再往下挖一层：
    /// &lt;adaptive-icon&gt;&lt;foreground android:drawable=...&gt; → 真正的图片
    /// </summary>
    private async Task<string?> FollowAsync(string apk, string? path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!path!.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return path;

        try
        {
            var xml = await ReadEntryAsync(apk, path, ct).ConfigureAwait(false);
            if (xml.Length < 8) return null;

            var (fgId, fgStr) = AxmlManifestResolver.FindAttr(xml, "foreground", "drawable");
            var inner = fgStr;

            if (inner == null && fgId != 0)
            {
                var arsc = await ReadEntryAsync(apk, "resources.arsc", ct).ConfigureAwait(false);
                var cands = ArscIconResolver.Resolve(arsc, fgId);
                inner = cands.FirstOrDefault(c =>
                            c.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            c.Path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                            c.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))?.Path;
            }

            if (string.IsNullOrEmpty(inner)) return null;
            return inner;
        }
        catch { return null; }
    }

    /// <summary>
    /// 把图标 XML 渲染成位图。支持 Android 常见的 drawable 包装：
    /// vector / adaptive-icon / layer-list / inset / bitmap / color / shape
    /// </summary>
    private async Task<Bitmap?> RenderIconXmlAsync(string apk, string xmlPath, CancellationToken ct)
    {
        try
        {
            var arsc = await ReadEntryAsync(apk, "resources.arsc", ct).ConfigureAwait(false);
            return await RenderDrawableAsync(apk, arsc, xmlPath, 192, 0, ct).ConfigureAwait(false);
        }
        catch { return null; }
    }

    private static bool IsImagePath(string p) =>
        p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        p.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
        p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private async Task<Bitmap?> RenderDrawableAsync(string apk, byte[] arsc, string path,
        int size, int depth, CancellationToken ct)
    {
        if (depth > 4) return null;

        if (IsImagePath(path))
        {
            var raw = await ReadEntryAsync(apk, path, ct).ConfigureAwait(false);
            return Decode(raw);
        }

        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return null;

        var xml = await ReadEntryAsync(apk, path, ct).ConfigureAwait(false);
        if (xml.Length < 8) return null;

        // ① 纯矢量
        if (VectorDrawableRenderer.IsVector(xml))
        {
            var png = VectorDrawableRenderer.RenderToPng(xml, size, id => ArscIconResolver.ResolveColor(arsc, id));
            if (png != null && HasVisiblePixels(png))
            {
                using var ms = new MemoryStream(png);
                return new Bitmap(ms);
            }
            return null;
        }

        var root = VectorDrawableRenderer.RootName(xml) ?? "";
        var attrs = AxmlManifestResolver.AllAttrs(xml);

        string? ResolvePath(uint id) =>
            ArscIconResolver.Resolve(arsc, id)
                .FirstOrDefault(c => IsImagePath(c.Path) || c.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))?.Path;

        switch (root)
        {
            case "adaptive-icon":
                {
                    var fgId = attrs.FirstOrDefault(a => a.elem == "foreground" && a.attr == "drawable").data;
                    var bgId = attrs.FirstOrDefault(a => a.elem == "background" && a.attr == "drawable").data;
                    if (fgId == 0) return null;

                    var fgPath = ResolvePath(fgId);
                    if (fgPath == null) return null;

                    var fgBmp = await RenderDrawableAsync(apk, arsc, fgPath, size, depth + 1, ct).ConfigureAwait(false);
                    if (fgBmp == null) return null;

                    uint? bgColor = bgId != 0 ? ArscIconResolver.ResolveColor(arsc, bgId) : null;
                    return ComposeAdaptive(fgBmp, bgColor, size);
                }

            case "layer-list":
                {
                    var items = attrs.Where(a => a.elem == "item" && a.attr == "drawable" && a.data != 0).ToList();
                    if (items.Count == 0) return null;

                    using var canvasBmp = new SKBitmap(size, size);
                    using var canvas = new SKCanvas(canvasBmp);
                    canvas.Clear(SKColors.Transparent);
                    var drew = false;

                    foreach (var it in items)
                    {
                        var p2 = ResolvePath(it.data);
                        if (p2 == null) continue;
                        var b = await RenderDrawableAsync(apk, arsc, p2, size, depth + 1, ct).ConfigureAwait(false);
                        if (b == null) continue;
                        using var sk = ToSkBitmap(b);
                        if (sk == null) continue;
                        canvas.DrawBitmap(sk, SKRect.Create(0, 0, size, size));
                        drew = true;
                    }
                    return drew ? Encode(canvasBmp) : null;
                }

            case "inset":
                {
                    var inner = attrs.FirstOrDefault(a => a.attr == "drawable" && a.data != 0).data;
                    if (inner == 0) return null;
                    var p2 = ResolvePath(inner);
                    if (p2 == null) return null;
                    var b = await RenderDrawableAsync(apk, arsc, p2, size, depth + 1, ct).ConfigureAwait(false);
                    if (b == null) return null;

                    var left = attrs.FirstOrDefault(a => a.attr == "insetLeft").data;
                    var top = attrs.FirstOrDefault(a => a.attr == "insetTop").data;
                    var right = attrs.FirstOrDefault(a => a.attr == "insetRight").data;
                    var bottom = attrs.FirstOrDefault(a => a.attr == "insetBottom").data;
                    // inset 的数值是 dimension 类型，这里按比例近似（不做单位换算）
                    var pad = Math.Min(size / 4, (int)Math.Max(left, Math.Max(top, Math.Max(right, bottom))) / 4);

                    using var canvasBmp2 = new SKBitmap(size, size);
                    using var canvas2 = new SKCanvas(canvasBmp2);
                    canvas2.Clear(SKColors.Transparent);
                    using var sk2 = ToSkBitmap(b);
                    if (sk2 == null) return null;
                    canvas2.DrawBitmap(sk2, SKRect.Create(pad, pad, size - pad * 2, size - pad * 2));
                    return Encode(canvasBmp2);
                }

            case "bitmap":
                {
                    var src = attrs.FirstOrDefault(a => a.attr == "src" && a.data != 0).data;
                    if (src == 0) return null;
                    var p2 = ResolvePath(src);
                    if (p2 == null) return null;
                    return await RenderDrawableAsync(apk, arsc, p2, size, depth + 1, ct).ConfigureAwait(false);
                }

            case "color":
                {
                    var id = attrs.FirstOrDefault(a => a.attr == "color" && a.data != 0).data;
                    var col = id != 0 ? ArscIconResolver.ResolveColor(arsc, id) : null;
                    if (col == null) return null;
                    using var canvasBmp3 = new SKBitmap(size, size);
                    using var canvas3 = new SKCanvas(canvasBmp3);
                    canvas3.Clear(SkColorOf(col.Value));
                    return Encode(canvasBmp3);
                }

            default:
                return null;
        }
    }

    private static Bitmap? ComposeAdaptive(Bitmap fg, uint? bgColor, int size)
    {
        try
        {
            using var sk = ToSkBitmap(fg);
            if (sk == null) return null;

            using var canvasBmp = new SKBitmap(size, size);
            using var canvas = new SKCanvas(canvasBmp);

            var radius = size * 72f / 108f / 2f;
            using var circle = new SKPath();
            circle.AddCircle(size / 2f, size / 2f, radius);

            canvas.Save();
            canvas.ClipPath(circle, SKClipOperation.Intersect, true);
            using var bgPaint = new SKPaint
            {
                IsAntialias = true,
                Color = bgColor.HasValue ? SkColorOf(bgColor.Value) : SKColors.White
            };
            canvas.DrawRect(SKRect.Create(0, 0, size, size), bgPaint);
            canvas.DrawBitmap(sk, SKRect.Create(0, 0, size, size));
            canvas.Restore();

            return Encode(canvasBmp);
        }
        catch { return null; }
    }

    private static SKColor SkColorOf(uint argb)
    {
        var a = (byte)((argb >> 24) & 0xFF);
        if (a == 0) a = 255;
        return new SKColor((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF), a);
    }

    private static SKBitmap? ToSkBitmap(Bitmap bmp)
    {
        try
        {
            using var ms = new MemoryStream();
            bmp.Save(ms);
            return SKBitmap.Decode(ms.ToArray());
        }
        catch { return null; }
    }

    private static Bitmap? Encode(SKBitmap sk)
    {
        try
        {
            using var img = SKImage.FromBitmap(sk);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data!.ToArray());
            return new Bitmap(ms);
        }
        catch { return null; }
    }

    /// <summary>判断渲染出来的图是不是"全透明"（等于没画出来）</summary>
    private static bool HasVisiblePixels(byte[] png)
    {
        try
        {
            using var bmp = SKBitmap.Decode(png);
            if (bmp == null) return false;
            for (var y = 0; y < bmp.Height; y += 2)
                for (var x = 0; x < bmp.Width; x += 2)
                    if (bmp.GetPixel(x, y).Alpha > 8) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>manifest 的 android:icon → resources.arsc → 图标文件路径</summary>
    private async Task<string?> ResolveIconPathAsync(string apk, CancellationToken ct)
    {
        try
        {
            var manifest = await ReadEntryAsync(apk, "AndroidManifest.xml", ct).ConfigureAwait(false);
            if (manifest.Length < 8) { IconDbg("  manifest 太短"); return null; }

            var (resId, directPath) = AxmlManifestResolver.ResolveIcon(manifest);
            IconDbg($"  manifest {manifest.Length}B resId=0x{resId:x8} direct={directPath}");
            if (!string.IsNullOrEmpty(directPath)) return await FollowAsync(apk, directPath!, ct).ConfigureAwait(false);
            if (resId == 0) return null;

            var arsc = await ReadEntryAsync(apk, "resources.arsc", ct).ConfigureAwait(false);
            if (arsc.Length < 12) return null;

            var candidates = ArscIconResolver.Resolve(arsc, resId);
            IconDbg($"  arsc {arsc.Length}B candidates={candidates.Count} first={(candidates.Count > 0 ? candidates[0].Path : "-")}");
            var first = candidates.FirstOrDefault(c =>
                c.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                c.Path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));

            return first?.Path ?? candidates.FirstOrDefault()?.Path;
        }
        catch (Exception ex)
        {
            IconDbg($"  resolve icon failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---------- 图标 ----------

    public async Task<(Bitmap? bmp, bool exact)> GetIconAsync(string pkg, string? apkPath = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            EnsureCacheVersion();
            var cache = Path.Combine(CacheDir, pkg + ".png");

            var cachedExact = File.Exists(Path.ChangeExtension(cache, ".exact"));

            if (File.Exists(cache))
            {
                var cached = await File.ReadAllBytesAsync(cache, ct).ConfigureAwait(false);
                if (IsPng(cached))
                {
                    try { return (new Bitmap(new MemoryStream(cached)), cachedExact); } catch { }
                }
                else
                {
                    try { File.Delete(cache); } catch { }   // 清掉以前存进去的坏数据
                }
            }

            // 负缓存：上次已经确认这个包取不到图标，短期内不再重复解析（重新解析要 unzip + 解 arsc）
            var missMarker = Path.ChangeExtension(cache, ".miss");
            try
            {
                if (File.Exists(missMarker) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(missMarker) < TimeSpan.FromDays(3))
                {
                    IconDbg($"[{pkg}] miss 缓存命中，跳过");
                    return (null, false);
                }
            }
            catch { }

            (Bitmap? bmp, bool exact) MarkMiss()
            {
                try { AtomicFile.WriteAllText(missMarker, DateTime.UtcNow.ToString("O")); } catch { }
                return (null, false);
            }
            void ClearMiss()
            {
                try { if (File.Exists(missMarker)) File.Delete(missMarker); } catch { }
            }

            await EnsureUnzipAsync(ct);
            IconDbg($"[{pkg}] unzip={_unzipExe} busybox={_unzipIsBusybox}");

            var apk = apkPath;
            if (string.IsNullOrEmpty(apk))
            {
                var pathRaw = await _fs.RunCommandAsync($"pm path {Sh.Q(pkg)} 2>/dev/null", false, ct)
                                        .ConfigureAwait(false);
                var apks = Regex.Matches(pathRaw, @"package:(\S+\.apk)");
                if (apks.Count == 0) return MarkMiss();
                apk = apks.Cast<Match>()
                          .Select(m => m.Groups[1].Value)
                          .FirstOrDefault(v => v.Contains("base.apk", StringComparison.OrdinalIgnoreCase))
                      ?? apks[0].Groups[1].Value;
            }

            // ① 先走"精确解析"：AndroidManifest 的 icon 引用 + resources.arsc
            var exact = await ResolveIconPathAsync(apk, ct).ConfigureAwait(false);
            IconDbg($"[{pkg}] exact={exact ?? "(null)"} apk={apk}");
            if (!string.IsNullOrEmpty(exact))
            {
                IconDbg($"[{pkg}] rendering xml={exact}");
#pragma warning disable CA2000 // 位图所有权交给调用方（作为 AppItem 图标显示），这里不能 Dispose
                var exactBmp = exact!.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    ? await RenderIconXmlAsync(apk, exact, ct).ConfigureAwait(false)
                    : Decode(await ReadEntryAsync(apk, exact, ct).ConfigureAwait(false));
#pragma warning restore CA2000

                IconDbg($"[{pkg}] exactBmp={(exactBmp == null ? "null" : exactBmp.PixelSize.Width + "px")}");
                if (exactBmp != null && exactBmp.PixelSize.Width >= 24)
                {
                    try
                    {
                        using var ms0 = new MemoryStream();
                        exactBmp.Save(ms0);
                        AtomicFile.WriteAllBytes(cache, ms0.ToArray());
                        AtomicFile.WriteAllText(Path.ChangeExtension(cache, ".exact"), "1");
                    }
                    catch (Exception ex) { AppLog.Add("写图标缓存失败：" + ex.Message); }
                    ClearMiss();
                    return (exactBmp, true);
                }
            }

            // ② 解析不出来再退回"按文件名打分"
            var listing = await _fs.RunCommandAsync(
                UnzipCmd($"-l {Sh.Q(apk)}") + " 2>/dev/null | grep -iE '\\.(png|webp)'", false, ct).ConfigureAwait(false);

            string? best = null;
            var bestScore = int.MinValue;
            var bestSize = -1L;

            foreach (var line in listing.Split('\n'))
            {
                var mm = Regex.Match(line.Trim(), @"^(\d+)\s+\S+\s+\S+\s+(.+)$");
                if (!mm.Success) continue;

                var size = long.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
                var name = mm.Groups[2].Value.Trim();
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) continue;

                var score = ScoreIcon(name);
                if (score < 0) continue;                        // 明确不是应用图标
                if (score == 0) continue;

                // 同分时取分辨率更高的（体积更大）。以前写的是 size > 0 —— 恒真，
                //   等价于"同分取最后一个"，而同分图标极常见（同一张图多套密度）。
                if (score > bestScore || (score == bestScore && size > bestSize))
                {
                    bestScore = score;
                    bestSize = size;
                    best = name;
                }
            }
            if (best == null) return (null, false);

            // argv 直传：参数分开给，不要加引号（没有 shell 参与）
            var args = _unzipIsBusybox
                ? new List<string> { "unzip", "-p", apk, best }
                : new List<string> { "-p", apk, best };

            // 先用普通权限（快），拿不到再提权
            var bytes = await _fs.ExecCaptureArgsAsync(_unzipExe!, args, false, ct).ConfigureAwait(false);
            if (!IsPng(bytes) && !IsWebp(bytes) && _fs.UseRoot)
                bytes = await _fs.ExecCaptureArgsAsync(_unzipExe!, args, true, ct).ConfigureAwait(false);

#pragma warning disable CA2000 // 同上：这张位图要交给 UI 显示，所有权不在这里
            var bmp = Decode(bytes);
#pragma warning restore CA2000
            if (bmp == null) return MarkMiss();                      // 防止把报错文本当图片
            if (bmp.PixelSize.Width < 24) return MarkMiss();         // 太小的多半是占位图

            // 统一转成 PNG 缓存（WebP 也能存）；这是猜出来的，必须清掉旧的 exact 标记
            try
            {
                using var ms = new MemoryStream();
                bmp.Save(ms);
                await File.WriteAllBytesAsync(cache, ms.ToArray(), ct).ConfigureAwait(false);
                File.Delete(Path.ChangeExtension(cache, ".exact"));
            }
            catch { }

            ClearMiss();
            return (bmp, false);      // 走到这里说明是"猜"出来的，标记为可能不准
        }
        catch (Exception ex)
        {
            IconDbg($"[{pkg}] icon failed: {ex.GetType().Name}: {ex.Message}");
            return (null, false);   // 异常不写负缓存：下次还有机会重试
        }
    }

    private static readonly object CacheVersionLock = new();
    private static bool _cacheVersionChecked;

    /// <summary>
    /// 缓存版本只做记录，不再删文件：图标读过一次就一直留着，刷新时同包名直接命中。
    /// 确实坏掉的旧数据由 GetIconAsync 里的 IsPng 检查就地清掉，不必整目录重来。
    /// </summary>
    private static void EnsureCacheVersion()
    {
        // 每个图标都 stat 一次 marker 太浪费；进程内只检查一次
        if (_cacheVersionChecked) return;

        lock (CacheVersionLock)
        {
            if (_cacheVersionChecked) return;
            try
            {
                var marker = Path.Combine(CacheDir, ".v9");
                if (!File.Exists(marker)) AtomicFile.WriteAllText(marker, "9");
            }
            catch (Exception ex) { AppLog.Add("写图标缓存版本标记失败：" + ex.Message); }
            finally { _cacheVersionChecked = true; }
        }
    }

    /// <summary>
    /// 图标候选打分：优先 mipmap + 名字含 launcher + 高密度；
    /// 排除 adaptive 图标的部件（background/foreground）和通知/启动图。
    /// </summary>
    private static int ScoreIcon(string name)
    {
        var low = name.ToLowerInvariant();
        if (!low.Contains("launcher") && !low.Contains("ic_")) return 0;

        // 明确不是"应用图标"的东西
        if (low.Contains("background") ||
            low.Contains("notification") || low.Contains("splash") ||
            low.Contains("banner") || low.Contains("logo_") ||
            low.Contains("shortcut") || low.Contains("status"))
            return -1;

        // 自适应图标的"前景层"：单独拿出来也是能认出的 Logo，作为次选
        if (low.Contains("foreground")) return 25;

        var score = 10;

        if (low.Contains("launcher")) score += 100;
        if (low.Contains("mipmap")) score += 60;
        else if (low.Contains("drawable")) score += 10;

        if (low.Contains("xxxhdpi")) score += 40;
        else if (low.Contains("xxhdpi")) score += 30;
        else if (low.Contains("xhdpi")) score += 20;
        else if (low.Contains("hdpi")) score += 10;
        else if (low.Contains("mdpi")) score += 5;

        if (low.Contains("ic_launcher.") || low.EndsWith("/ic_launcher.png")) score += 50;
        if (low.Contains("round")) score -= 5;      // 圆形版本通常更小/带透明边

        return score;
    }

    private static bool IsPng(byte[] b) =>
        b.Length > 64 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    private static bool IsWebp(byte[] b) =>
        b.Length > 64 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
        && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';

    /// <summary>PNG 直接读；WebP 用 Skia 解码后转成 Avalonia 位图</summary>
    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            if (IsPng(bytes))
            {
                using var ms = new MemoryStream(bytes);
                return new Bitmap(ms);
            }

            var sk = SKBitmap.Decode(bytes);
            if (sk == null) return null;
            using (sk)
            {
                using var img = SKImage.FromBitmap(sk);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var ms2 = new MemoryStream(data.ToArray());
                return new Bitmap(ms2);
            }
        }
        catch { return null; }
    }
}
