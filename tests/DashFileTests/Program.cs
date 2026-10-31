using System.Diagnostics;
using System.Reflection;
using System.Text;
using DashFile.Services;

var fail = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
    if (!ok) fail++;
}

// ---------- 1) SVG pathData 死循环 ----------
var sw = Stopwatch.StartNew();
var p = DashFile.Services.SvgPathParser.Parse("M0 0z5");
sw.Stop();
Check("svg 'M0 0z5' 在 1.5s 内返回", sw.ElapsedMilliseconds < 1500, sw.ElapsedMilliseconds + "ms");
Check("svg 'M0 0z5' 仍产出路径", p != null && p.PointCount >= 1, p == null ? "null" : "pts=" + p.PointCount);

sw.Restart();
var p2 = DashFile.Services.SvgPathParser.Parse("M10 10 L20 20 Z");
sw.Stop();
Check("svg 正常路径", p2 != null && sw.ElapsedMilliseconds < 500, sw.ElapsedMilliseconds + "ms");

sw.Restart();
var p3 = DashFile.Services.SvgPathParser.Parse("M0 0 Z Z Z Z 5 6 7 garbage $$$ 8");
sw.Stop();
Check("svg 尾部垃圾不死循环", sw.ElapsedMilliseconds < 1500, sw.ElapsedMilliseconds + "ms");

// ---------- 2) 授权表布局识别 ----------
var asm = typeof(DashFile.Services.SuperuserService).Assembly;
var suType = asm.GetType("DashFile.Services.SuperuserService")!;
var layoutType = suType.GetNestedType("Layout", BindingFlags.NonPublic)!;
var detect = suType.GetMethod("DetectLayout", BindingFlags.NonPublic | BindingFlags.Instance)!;

object Probe(byte[] bytes, string variant)
{
    var fsType = asm.GetType("DashFile.Services.RemoteFileSystem")!;
    var adbType = asm.GetType("DashFile.Services.AdbRunner")!;
    var adb = Activator.CreateInstance(adbType, "adb.exe")!;
    var fs = Activator.CreateInstance(fsType, adb, "SERIAL", true)!;
    var svc = Activator.CreateInstance(suType, fs, variant)!;
    return detect.Invoke(svc, new object[] { bytes })!;
}

string Describe(object layout)
{
    if (layout == null) return "null";
    string F(string n) => layoutType.GetProperty(n)!.GetValue(layout)!.ToString()!;
    return "header=" + F("HeaderLen") + " record=" + F("RecordLen") + " pkgOff=" + F("PackageOffset") + " count=" + F("Count");
}

// 新版：头 8 + 记录 784（前 4 字节前缀），1 条记录 -> 总长 792，与老版同长
var rec = new byte[784];
BitConverter.GetBytes(1).CopyTo(rec, 0);
Encoding.UTF8.GetBytes("com.test.app").CopyTo(rec, 4);
BitConverter.GetBytes(10123).CopyTo(rec, 4 + 256);
BitConverter.GetBytes(1).CopyTo(rec, 4 + 260);
var newFile = new byte[792];
Encoding.ASCII.GetBytes("USK").CopyTo(newFile, 0);
BitConverter.GetBytes(1).CopyTo(newFile, 4);
rec.CopyTo(newFile, 8);

var a = Describe(Probe(newFile, "KernelSU-Next"));
Check("新版单条表 + KernelSU-Next -> 8/784/4", a.Contains("header=8") && a.Contains("record=784") && a.Contains("pkgOff=4"), a);

var bdesc = Describe(Probe(newFile, "KernelSU"));
Check("同一份数据 + KernelSU -> 12/780/0（说明该布局确实有歧义）", bdesc.Contains("header=12") && bdesc.Contains("record=780"), bdesc);

// 老版：头 12 + 记录 780，1 条记录
var oldRec = new byte[780];
Encoding.UTF8.GetBytes("com.test.app").CopyTo(oldRec, 0);
BitConverter.GetBytes(10123).CopyTo(oldRec, 256);
BitConverter.GetBytes(1).CopyTo(oldRec, 260);
var oldFile = new byte[792];
Encoding.ASCII.GetBytes("USK").CopyTo(oldFile, 0);
BitConverter.GetBytes(1).CopyTo(oldFile, 8);
oldRec.CopyTo(oldFile, 12);

var c = Describe(Probe(oldFile, "KernelSU"));
Check("老版单条表 + KernelSU -> 12/780/0", c.Contains("header=12") && c.Contains("record=780") && c.Contains("pkgOff=0"), c);

// 新版两条记录：总长 8+1568=1576，唯一解
var twoRec = new byte[784 * 2];
rec.CopyTo(twoRec, 0);
var rec2 = new byte[784];
BitConverter.GetBytes(1).CopyTo(rec2, 0);
Encoding.UTF8.GetBytes("org.other.app").CopyTo(rec2, 4);
BitConverter.GetBytes(10500).CopyTo(rec2, 4 + 256);
rec2.CopyTo(twoRec, 784);
var twoFile = new byte[1576];
Encoding.ASCII.GetBytes("USK").CopyTo(twoFile, 0);
BitConverter.GetBytes(2).CopyTo(twoFile, 4);
twoRec.CopyTo(twoFile, 8);
var d = Describe(Probe(twoFile, "KernelSU-Next"));
Check("新版两条表 -> 8/784/4 count=2", d.Contains("header=8") && d.Contains("count=2"), d);

// ---------- 2b) 重排往返：新增一条后，字节流还能不能被"设备侧"重新解析 ----------
// 这是 P0-1 真正会毁数据的地方：DetectLayout 读对不代表 WriteAsync 重排后还对。
var compose = suType.GetMethod("ComposeTable", BindingFlags.NonPublic | BindingFlags.Static)!;
var entryType = suType.GetNestedType("Entry", BindingFlags.Public)!;

object MakeEntry(string pkg, int uid, byte[] raw)
    => Activator.CreateInstance(entryType, pkg, uid, true, raw)!;

bool[] ambiguous = new bool[2];
System.Collections.IList EntryList(params object[] items)
{
    var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
    foreach (var i in items) list.Add(i);
    return list;
}
string PkgAt(byte[] buf, int off)
{
    var end = Array.IndexOf(buf, (byte)0, off, 256);
    return Encoding.UTF8.GetString(buf, off, (end < 0 ? 256 : end - off));
}

// A. 新版：单条 -> 追加一条 -> 总长必须是 1576，且第 2 条包名落在 8+784+4=796
var layoutNew = Probe(newFile, "KernelSU-Next");
var composed = (byte[])compose.Invoke(null, new object[] { layoutNew, EntryList(MakeEntry("com.test.app", 10123, rec), MakeEntry("org.other.app", 10500, rec2)) })!;
var reNew = Describe(Probe(composed, "KernelSU-Next"));
Check("新版追加后：总长 1576 且仍是 8/784/4 count=2",
    composed.Length == 1576 && reNew.Contains("header=8") && reNew.Contains("count=2"), composed.Length + " | " + reNew);
Check("新版追加后：第 2 条包名落在 796", PkgAt(composed, 8 + 784 + 4) == "org.other.app", PkgAt(composed, 8 + 784 + 4));

// B. 老版：单条 -> 追加一条 -> 总长必须是 1572，第 2 条包名落在 12+780=792
var layoutOld = Probe(oldFile, "KernelSU");
var oldRec2 = new byte[780];
Encoding.UTF8.GetBytes("org.other.app").CopyTo(oldRec2, 0);
BitConverter.GetBytes(10500).CopyTo(oldRec2, 256);
var composedOld = (byte[])compose.Invoke(null, new object[] { layoutOld, EntryList(MakeEntry("com.test.app", 10123, oldRec), MakeEntry("org.other.app", 10500, oldRec2)) })!;
var reOld = Describe(Probe(composedOld, "KernelSU"));
Check("老版追加后：总长 1572 且仍是 12/780/0 count=2",
    composedOld.Length == 1572 && reOld.Contains("header=12") && reOld.Contains("count=2"), composedOld.Length + " | " + reOld);
Check("老版追加后：第 2 条包名落在 792", PkgAt(composedOld, 12 + 780) == "org.other.app", PkgAt(composedOld, 12 + 780));

// C. 原地改 allow（不增删）：拼出来必须与原文件逐字节相同 —— 这正是"歧义下也能安全改开关"的依据
var sameNew = (byte[])compose.Invoke(null, new object[] { layoutNew, EntryList(MakeEntry("com.test.app", 10123, rec)) })!;
Check("新版原地改：重排结果与原文件逐字节一致", sameNew.SequenceEqual(newFile), sameNew.Length + " vs " + newFile.Length);
var sameOld = (byte[])compose.Invoke(null, new object[] { layoutOld, EntryList(MakeEntry("com.test.app", 10123, oldRec)) })!;
Check("老版原地改：重排结果与原文件逐字节一致", sameOld.SequenceEqual(oldFile), sameOld.Length + " vs " + oldFile.Length);

// D. 歧义标记：单条表两解同长 -> true；两条表 -> false
var ambProp = suType.GetProperty("LayoutAmbiguous")!;
var svcNew = Probe2(newFile, "KernelSU-Next", out var ambN);
var svcTwo = Probe2(twoFile, "KernelSU-Next", out var ambT);
Check("单条表被标记为布局歧义", ambN, "ambiguous=" + ambN);
Check("两条表不再歧义", !ambT, "ambiguous=" + ambT);

object Probe2(byte[] bytes, string variant, out bool ambiguous)
{
    var fsType2 = asm.GetType("DashFile.Services.RemoteFileSystem")!;
    var adbType2 = asm.GetType("DashFile.Services.AdbRunner")!;
    var adb2 = Activator.CreateInstance(adbType2, "adb.exe")!;
    var fs2 = Activator.CreateInstance(fsType2, adb2, "SERIAL", true)!;
    var svc = Activator.CreateInstance(suType, fs2, variant)!;
    detect.Invoke(svc, new object[] { bytes });
    ambiguous = (bool)ambProp.GetValue(svc)!;
    return svc;
}

// ---------- 3) 插件扫描 / 数据目录分离 ----------
var tmpRoot = Path.Combine(Path.GetTempPath(), "dsh-plugtest-" + Guid.NewGuid().ToString("N"));
var plugins = Path.Combine(tmpRoot, "plugins");
void MakePlugin(string dirName, string idJson)
{
    var d = Path.Combine(plugins, dirName);
    Directory.CreateDirectory(d);
    File.WriteAllText(Path.Combine(d, "plugin.json"),
        "{\"id\":\"" + idJson + "\",\"name\":\"Demo\",\"assembly\":\"nope.dll\",\"enabled\":false}");
}
MakePlugin("com.demo.ok", "com.demo.ok");
MakePlugin("com.demo.ok.bak-abc123", "com.demo.ok");       // 覆盖安装的备份目录
MakePlugin("com.demo.ok.deleted-def456", "com.demo.ok");   // 删除失败的残留目录
MakePlugin(".install-ghi789", "com.demo.ok");              // 安装临时目录
MakePlugin("evil-dir", "../../../evil");                    // 恶意 id
Directory.CreateDirectory(Path.Combine(tmpRoot, "plugin-data", "com.demo.ok"));
File.WriteAllText(Path.Combine(tmpRoot, "plugin-data", "com.demo.ok", "settings.json"), "{}");

var pmType = typeof(DashFile.Services.Plugins.PluginManager);
var pm = new DashFile.Services.Plugins.PluginManager(
    "1.0.0", new[] { plugins }, tmpRoot,
    (m, i) => throw new InvalidOperationException("测试里不加载插件"));
await pm.LoadAllAsync();

var okCount = pm.Plugins.Count(p => p.Id == "com.demo.ok");
Check("bak/deleted/install 目录不被扫描（同名 id 只算 1 个）", okCount == 1, "count=" + okCount);
Check("恶意 id(..) 不进入插件列表", !pm.Plugins.Any(p => p.Id.Contains("..")), string.Join("|", pm.Plugins.Select(p => p.Id)));

var dataDirProp = pmType.GetProperty("DataDirectory")!;
var dataDir = (string)dataDirProp.GetValue(pm)!;
Check("插件数据目录已与扫描根分离", dataDir == Path.Combine(tmpRoot, "plugin-data") && !dataDir.StartsWith(plugins), dataDir);
Check("plugin-state.json 不再落在扫描根里", !File.Exists(Path.Combine(plugins, "plugin-state.json")));

await pm.UninstallAsync("com.demo.ok", deleteData: false);
Check("卸载(保留数据)：插件目录被删", !Directory.Exists(Path.Combine(plugins, "com.demo.ok")));
Check("卸载(保留数据)：插件数据仍在", File.Exists(Path.Combine(tmpRoot, "plugin-data", "com.demo.ok", "settings.json")));

Directory.CreateDirectory(Path.Combine(plugins, "com.demo.ok2"));
MakePlugin("com.demo.ok2", "com.demo.ok2");
Directory.CreateDirectory(Path.Combine(tmpRoot, "plugin-data", "com.demo.ok2"));
File.WriteAllText(Path.Combine(tmpRoot, "plugin-data", "com.demo.ok2", "settings.json"), "{}");
await pm.UninstallAsync("com.demo.ok2", deleteData: true);
Check("卸载(删数据)：插件数据被删除", !Directory.Exists(Path.Combine(tmpRoot, "plugin-data", "com.demo.ok2")));

try { Directory.Delete(tmpRoot, true); } catch { }

// ---------- 5) 合并后的 AXML 阅读器（自建二进制 XML） ----------
var axml = BuildAxml();
var doc = AxmlReader.Parse(axml);
Check("AXML: 能解析出根元素 manifest", doc != null && doc!.Name == "manifest", doc?.Name ?? "null");
var appEl = AxmlReader.Find(doc, "application");
Check("AXML: 字符串池 UTF-8 中文解码正确",
    AxmlReader.Attr(appEl, "label") == "演示应用", "'" + AxmlReader.Attr(appEl, "label") + "'");
Check("AXML: 资源引用型属性（@0x7f…）", AxmlReader.Attr(doc, "versionCode") == "@0x00000007", AxmlReader.Attr(doc, "versionCode"));

var perms = AxmlReader.FindAll(doc, "uses-permission").Select(p => AxmlReader.Attr(p, "name")).ToList();
Check("AXML: FindAll 按文档顺序取到 2 个 uses-permission",
    perms.Count == 2 && perms[0] == "android.permission.CAMERA" && perms[1] == "android.permission.INTERNET",
    string.Join(" | ", perms));

var icon = AxmlReader.ResolveIcon(axml);
Check("AXML: ResolveIcon 取到 application/android:icon 的资源 ID", icon.resId == 0x7f0e0002 && icon.directPath == null,
    "id=0x" + icon.resId.ToString("x8") + " path=" + (icon.directPath ?? "null"));

var findAttr = AxmlReader.FindAttr(axml, "application", "icon");
Check("AXML: FindAttr(application, icon) 返回资源 ID", findAttr.resId == 0x7f0e0002 && findAttr.str == null,
    "id=0x" + findAttr.resId.ToString("x8"));

var all = AxmlReader.AllAttrs(axml);
Check("AXML: AllAttrs 覆盖全部属性且不含 xmlns", all.Count == 6 && !all.Any(a => a.attr.StartsWith("xmlns")),
    "count=" + all.Count + " : " + string.Join(",", all.Select(a => a.elem + "/" + a.attr)));

var xml = AxmlReader.ToXml(doc!);
Check("AXML: ToXml 带 android 命名空间且值已转义",
    xml.Contains("<manifest xmlns:android=") && xml.Contains("label=\"演示应用\""), xml.Split('\n')[1]);

// 畸形输入：不应该抛异常，也不应该崩
var trunc = (byte[])axml.Clone();
trunc[12] = 0; trunc[13] = 0; trunc[14] = 0; trunc[15] = 0;            // 把字符串池 chunk 的 size 抹成 0
var t1 = AxmlReader.Parse(trunc);
Check("AXML: chunk 长度被抹成 0 时返回 null 而不是抛异常", t1 == null, t1?.Name ?? "null");

var neg = (byte[])axml.Clone();
var elPos = FindFirstChunk(neg, 0x0102);
BitConverter.GetBytes(0x80000000u).CopyTo(neg, elPos + 20);            // 元素名下标改成 0x80000000
var t2 = AxmlReader.Parse(neg);
Check("AXML: 负的元素名下标不抛异常（名字退化为空）", t2 != null && t2!.Name == "", "'" + (t2?.Name ?? "null") + "'");

var deep = BuildDeepAxml(400);
var t3 = AxmlReader.Parse(deep);
var depthOk = false;
try { _ = AxmlReader.ToXml(t3!); depthOk = true; } catch { }
Check("AXML: 400 层嵌套不崩（深度截断 + 序列化不递归爆栈）", t3 != null && depthOk, t3 == null ? "null" : "ok");

// 字符串池：AXML 与 resources.arsc 现在共用同一份解码（以前是第 4 份拷贝）
var poolChunk = BuildPool(new List<string> { "res/drawable/ic_launcher.png", "演示" });
var poolStrings = AxmlReader.ReadStringPool(poolChunk, 0);
Check("字符串池: AXML/arsc 共用解码（路径 + 中文）",
    poolStrings.Count == 2 && poolStrings[0].EndsWith(".png") && poolStrings[1] == "演示",
    string.Join(" | ", poolStrings));

// 三份实现已合并：旧入口必须还活着（AppService / OpenWith 的调用点没改）
Check("AXML: AxmlManifestResolver 薄封装仍可用",
    AxmlManifestResolver.ResolveIcon(axml).resId == 0x7f0e0002 &&
    AxmlManifestResolver.AllAttrs(axml).Count == all.Count,
    "ok");

// ================= AXML 测试用的最小二进制 XML 建造器 =================

void WriteU16(List<byte> b, int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); }
void WriteU32(List<byte> b, uint v)
{
    b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF));
    b.Add((byte)((v >> 16) & 0xFF)); b.Add((byte)((v >> 24) & 0xFF));
}

byte[] BuildPool(List<string> strings)
{
    var pool = new List<byte>();
    var offsets = new List<int>();
    foreach (var s in strings)
    {
        offsets.Add(pool.Count);
        var bytes = Encoding.UTF8.GetBytes(s);
        var u16 = s.Length;
        if (u16 > 0x7F) { pool.Add((byte)(0x80 | (u16 >> 8))); pool.Add((byte)(u16 & 0xFF)); }
        else pool.Add((byte)u16);
        if (bytes.Length > 0x7F) { pool.Add((byte)(0x80 | (bytes.Length >> 8))); pool.Add((byte)(bytes.Length & 0xFF)); }
        else pool.Add((byte)bytes.Length);
        pool.AddRange(bytes);
        pool.Add(0);
    }

    var chunk = new List<byte>();
    int stringsStart = 28 + 4 * strings.Count;
    WriteU16(chunk, 0x0001); WriteU16(chunk, 28); WriteU32(chunk, (uint)(stringsStart + pool.Count));
    WriteU32(chunk, (uint)strings.Count); WriteU32(chunk, 0); WriteU32(chunk, 0x100);
    WriteU32(chunk, (uint)stringsStart); WriteU32(chunk, 0);
    foreach (var o in offsets) WriteU32(chunk, (uint)o);
    chunk.AddRange(pool);
    return chunk.ToArray();
}

int FindFirstChunk(byte[] buf, ushort type)
{
    int p = 8;
    while (p + 8 <= buf.Length)
    {
        int t = buf[p] | (buf[p + 1] << 8);
        int size = BitConverter.ToInt32(buf, p + 4);
        if (size <= 0) return -1;
        if (t == type) return p;
        p += size;
    }
    return -1;
}

byte[] BuildAxml()
{
    var strings = new List<string>
    {
        "manifest", "package", "com.demo.app", "versionCode", "application", "icon",
        "uses-permission", "name", "android.permission.CAMERA", "android.permission.INTERNET",
        "label", "演示应用", "android", "xmlns",
    };
    int Idx(string s) => strings.IndexOf(s);

    List<byte> Start(string name, params (string n, byte t, uint d)[] attrs)
    {
        var c = new List<byte>();
        WriteU16(c, 0x0102); WriteU16(c, 16); WriteU32(c, (uint)(16 + 20 + 20 * attrs.Length));
        WriteU32(c, 1); WriteU32(c, 0xFFFFFFFF);
        WriteU32(c, 0xFFFFFFFF); WriteU32(c, (uint)Idx(name));
        WriteU16(c, 20); WriteU16(c, 20); WriteU16(c, attrs.Length);
        WriteU16(c, 0); WriteU16(c, 0); WriteU16(c, 0);
        foreach (var a in attrs)
        {
            WriteU32(c, 0xFFFFFFFF); WriteU32(c, (uint)Idx(a.n)); WriteU32(c, 0xFFFFFFFF);
            WriteU16(c, 8); c.Add(0); c.Add(a.t); WriteU32(c, a.d);
        }
        return c;
    }

    List<byte> End(string name)
    {
        var c = new List<byte>();
        WriteU16(c, 0x0103); WriteU16(c, 16); WriteU32(c, 24);
        WriteU32(c, 1); WriteU32(c, 0xFFFFFFFF);
        WriteU32(c, 0xFFFFFFFF); WriteU32(c, (uint)Idx(name));
        return c;
    }

    var body = new List<byte>();
    body.AddRange(BuildPool(strings));
    body.AddRange(Start("manifest",
        ("xmlns", (byte)0x03, (uint)Idx("android")),
        ("package", (byte)0x03, (uint)Idx("com.demo.app")),
        ("versionCode", (byte)0x01, 7u)));
    body.AddRange(Start("application",
        ("icon", (byte)0x01, 0x7f0e0002u),
        ("label", (byte)0x03, (uint)Idx("演示应用"))));
    body.AddRange(Start("uses-permission", ("name", (byte)0x03, (uint)Idx("android.permission.CAMERA"))));
    body.AddRange(End("uses-permission"));
    body.AddRange(Start("uses-permission", ("name", (byte)0x03, (uint)Idx("android.permission.INTERNET"))));
    body.AddRange(End("uses-permission"));
    body.AddRange(End("application"));
    body.AddRange(End("manifest"));

    var file = new List<byte>();
    WriteU16(file, 0x0003); WriteU16(file, 8); WriteU32(file, (uint)(8 + body.Count));
    file.AddRange(body);
    return file.ToArray();
}

byte[] BuildDeepAxml(int depth)
{
    var strings = new List<string> { "a" };
    var body = new List<byte>();
    body.AddRange(BuildPool(strings));

    for (int i = 0; i < depth; i++)
    {
        var c = new List<byte>();
        WriteU16(c, 0x0102); WriteU16(c, 16); WriteU32(c, 36);
        WriteU32(c, 1); WriteU32(c, 0xFFFFFFFF);
        WriteU32(c, 0xFFFFFFFF); WriteU32(c, 0);
        WriteU16(c, 20); WriteU16(c, 20); WriteU16(c, 0);
        WriteU16(c, 0); WriteU16(c, 0); WriteU16(c, 0);
        body.AddRange(c);
    }
    for (int i = 0; i < depth; i++)
    {
        var c = new List<byte>();
        WriteU16(c, 0x0103); WriteU16(c, 16); WriteU32(c, 24);
        WriteU32(c, 1); WriteU32(c, 0xFFFFFFFF);
        WriteU32(c, 0xFFFFFFFF); WriteU32(c, 0);
        body.AddRange(c);
    }

    var file = new List<byte>();
    WriteU16(file, 0x0003); WriteU16(file, 8); WriteU32(file, (uint)(8 + body.Count));
    file.AddRange(body);
    return file.ToArray();
}

Console.WriteLine();
Console.WriteLine(fail == 0 ? "ALL PASS" : fail + " FAILED");

return fail;