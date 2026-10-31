using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DashFile.Services;

namespace DashFile.Services.OpenWith;

/// <summary>APK 只读逆向分析：Manifest / 签名 / DEX / 资源字符串 / 哈希。</summary>
internal sealed class ApkAnalysis
{
    public string FilePath = "";
    public string FileName = "";
    public long FileSize;
    public string Md5 = "";
    public string Sha1 = "";
    public string Sha256 = "";
    public int EntryCount;
    public string ParseError = "";

    public List<string> EntryNames = new();
    public List<(string Name, long Size)> DexFiles = new();
    public List<string> Abis = new();
    public List<string> Densities = new();
    public List<string> V1SigFiles = new();
    public List<string> Schemes = new();

    public string SignerSubject = "";
    public string SignerIssuer = "";
    public string SignerSha256 = "";
    public string SignerValid = "";

    public string Package = "";
    public string VersionName = "";
    public string VersionCode = "";
    public string MinSdk = "";
    public string TargetSdk = "";
    public string AppLabel = "";
    public string AppDebuggable = "";
    public string AppAllowBackup = "";
    public string ManifestXml = "";

    public List<string> Permissions = new();
    public List<string> Activities = new();
    public List<string> Services = new();
    public List<string> Receivers = new();
    public List<string> Providers = new();

    public List<DexStat> DexStats = new();
    public List<string> DexStrings = new();
    public List<string> ResourceStrings = new();
    public List<(string Name, long Size)> Largest = new();

    public sealed class DexStat
    {
        public string Name = "";
        public long Size;
        public int Version;
        public int StringIds;
        public int TypeIds;
        public int ProtoIds;
        public int FieldIds;
        public int MethodIds;
        public int ClassDefs;
    }

    public static ApkAnalysis Build(string path, Action<string>? progress = null)
    {
        var a = new ApkAnalysis
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
        };
        try
        {
            var fi = new FileInfo(path);
            a.FileSize = fi.Length;
            progress?.Invoke("计算哈希…");
            using (var fs = File.OpenRead(path))
            {
                using var md5 = MD5.Create();
                using var sha1 = SHA1.Create();
                using var sha256 = SHA256.Create();
                var buf = new byte[81920];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    md5.TransformBlock(buf, 0, n, null, 0);
                    sha1.TransformBlock(buf, 0, n, null, 0);
                    sha256.TransformBlock(buf, 0, n, null, 0);
                }
                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                a.Md5 = Hex(md5.Hash);
                a.Sha1 = Hex(sha1.Hash);
                a.Sha256 = Hex(sha256.Hash);
            }

            progress?.Invoke("读取 APK 结构…");
            var dexBuffers = new List<(string Name, byte[] Data)>();
            using (var zip = ZipFile.OpenRead(path))
            {
                a.EntryCount = zip.Entries.Count;
                foreach (var e in zip.Entries)
                {
                    a.EntryNames.Add(e.FullName);
                    var lower = e.FullName.ToLowerInvariant();
                    if (lower.StartsWith("lib/", StringComparison.Ordinal))
                    {
                        var seg = e.FullName.Split('/');
                        if (seg.Length > 1 && seg[1].Length > 0 && !a.Abis.Contains(seg[1])) a.Abis.Add(seg[1]);
                    }
                    if ((lower.StartsWith("res/", StringComparison.Ordinal) || lower.StartsWith("r/", StringComparison.Ordinal)) && lower.Contains("-"))
                    {
                        var seg = e.FullName.Split('/');
                        if (seg.Length > 1)
                        {
                            var dash = seg[1].IndexOf('-');
                            if (dash > 0)
                            {
                                var dpi = seg[1].Substring(dash + 1);
                                if (dpi.EndsWith("dpi", StringComparison.Ordinal) && !a.Densities.Contains(dpi)) a.Densities.Add(dpi);
                            }
                        }
                    }
                    if (lower.StartsWith("meta-inf/", StringComparison.Ordinal) &&
                        (lower.EndsWith(".rsa", StringComparison.Ordinal) || lower.EndsWith(".dsa", StringComparison.Ordinal) ||
                         lower.EndsWith(".ec", StringComparison.Ordinal)))
                    {
                        a.V1SigFiles.Add(e.FullName);
                        try
                        {
                            using var s = e.Open();
                            using var ms = new MemoryStream();
                            s.CopyTo(ms);
                            ReadCertificate(a, ms.ToArray());
                        }
                        catch { }
                    }
                    if (lower.StartsWith("classes", StringComparison.Ordinal) && lower.EndsWith(".dex", StringComparison.Ordinal))
                    {
                        a.DexFiles.Add((e.FullName, e.Length));
                        if (e.Length > 0 && e.Length < 96L * 1024 * 1024)
                        {
                            try
                            {
                                using var s = e.Open();
                                using var ms = new MemoryStream();
                                s.CopyTo(ms);
                                dexBuffers.Add((e.FullName, ms.ToArray()));
                            }
                            catch { }
                        }
                    }
                }

                a.Abis.Sort(StringComparer.OrdinalIgnoreCase);
                a.Densities.Sort(StringComparer.OrdinalIgnoreCase);

                // Manifest / 资源表 / 最大条目
                var manifestEntry = zip.GetEntry("AndroidManifest.xml");
                if (manifestEntry != null)
                {
                    try
                    {
                        using var s = manifestEntry.Open();
                        var doc = AxmlReader.Parse(ReadEntryCapped(s, manifestEntry.Length, 16 * 1024 * 1024, "AndroidManifest.xml"));
                        if (doc != null)
                        {
                            a.ApplyManifest(doc);
                            a.ManifestXml = doc.ToXml();
                        }
                    }
                    catch (Exception ex) { a.ParseError += "Manifest: " + ex.Message + "\n"; }
                }

                var arsc = zip.GetEntry("resources.arsc");
                if (arsc != null)
                {
                    try
                    {
                        using var s = arsc.Open();
                        a.ResourceStrings = Arsc.ExtractStrings(
                            ReadEntryCapped(s, arsc.Length, 64 * 1024 * 1024, "resources.arsc"));
                    }
                    catch (Exception ex) { a.ParseError += "resources.arsc: " + ex.Message + "\n"; }
                }

                a.Largest = zip.Entries.OrderByDescending(e => e.Length).Take(10)
                                .Select(e => (e.FullName, e.Length)).ToList();
            }

            progress?.Invoke("解析 DEX…");
            foreach (var d in dexBuffers)
            {
                try
                {
                    var stat = Dex.ReadStat(d.Name, d.Data);
                    a.DexStats.Add(stat);
                    a.DexStrings.AddRange(Dex.ReadStrings(d.Data));
                }
                catch (Exception ex) { a.ParseError += d.Name + ": " + ex.Message + "\n"; }
            }

            if (a.DexStats.Count > 0 && a.DexStrings.Count == 0)
            {
                // DEX 解析失败时也留个可用的空结果
            }

            progress?.Invoke("检查签名方案…");
            try { a.DetectSigningSchemes(); } catch { }
        }
        catch (Exception ex)
        {
            a.ParseError += ex.Message + "\n";
        }
        return a;
    }

    private void ApplyManifest(AxmlElement root)
    {
        Package = AxmlReader.Attr(root, "package");
        VersionName = AxmlReader.Attr(root, "versionName");
        VersionCode = AxmlReader.Attr(root, "versionCode");
        var sdk = AxmlReader.Find(root, "uses-sdk");
        if (sdk != null)
        {
            MinSdk = AxmlReader.Attr(sdk, "minSdkVersion");
            TargetSdk = AxmlReader.Attr(sdk, "targetSdkVersion");
        }
        foreach (var p in AxmlReader.FindAll(root, "uses-permission"))
        {
            var n = AxmlReader.Attr(p, "name");
            if (!string.IsNullOrWhiteSpace(n) && !Permissions.Contains(n)) Permissions.Add(n);
        }
        var app = AxmlReader.Find(root, "application");
        if (app != null)
        {
            AppLabel = AxmlReader.Attr(app, "label");
            AppDebuggable = AxmlReader.Attr(app, "debuggable");
            AppAllowBackup = AxmlReader.Attr(app, "allowBackup");
            foreach (var child in app.Children)
            {
                var name = Expand(AxmlReader.Attr(child, "name"));
                if (string.IsNullOrWhiteSpace(name)) continue;
                switch (child.Name)
                {
                    case "activity": Activities.Add(name); break;
                    case "service": Services.Add(name); break;
                    case "receiver": Receivers.Add(name); break;
                    case "provider": Providers.Add(name); break;
                }
            }
        }
        Permissions.Sort(StringComparer.Ordinal);
    }

    private string Expand(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.StartsWith(".", StringComparison.Ordinal)) return Package + name;
        if (!name.Contains('.')) return Package + "." + name;
        return name;
    }

    private static void ReadCertificate(ApkAnalysis a, byte[] pkcs7)
    {
        try
        {
            var col = new X509Certificate2Collection();
            col.Import(pkcs7);
            foreach (var cert in col)
            {
                if (!string.IsNullOrEmpty(a.SignerSubject)) break;
                a.SignerSubject = cert.Subject;
                a.SignerIssuer = cert.Issuer;
                a.SignerSha256 = cert.GetCertHashString(HashAlgorithmName.SHA256);
                a.SignerValid = cert.NotBefore.ToString("yyyy-MM-dd") + " ~ " + cert.NotAfter.ToString("yyyy-MM-dd");
            }
        }
        catch { }
    }

    /// <summary>读取 APK Signing Block，判断 v2/v3 签名方案。</summary>
    private void DetectSigningSchemes()
    {
        using var fs = File.OpenRead(FilePath);
        var len = fs.Length;
        if (len < 32) return;
        var tail = new byte[Math.Min(65557, len)];
        fs.Seek(len - tail.Length, SeekOrigin.Begin);
        fs.ReadExactly(tail, 0, tail.Length);

        int eocd = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
            {
                eocd = i;
                break;
            }
        }
        if (eocd < 0) return;
        long cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
        if (cdOffset < 32 || cdOffset > len) return;

        var buf = new byte[24];
        fs.Seek(cdOffset - 24, SeekOrigin.Begin);
        fs.ReadExactly(buf, 0, 24);
        var magic = Encoding.ASCII.GetString(buf, 8, 16);
        if (magic != "APK Sig Block 42") return;

        long size = (long)BitConverter.ToUInt64(buf, 0);
        long blockStart = cdOffset - size - 8;
        if (blockStart < 24 || size > len) return;

        fs.Seek(blockStart + 8, SeekOrigin.Begin);
        long pairEnd = cdOffset - 24;
        long pos = blockStart + 8;
        while (pos + 12 <= pairEnd)
        {
            var head = new byte[8];
            fs.ReadExactly(head, 0, 8);
            long pairLen = (long)BitConverter.ToUInt64(head, 0);
            if (pairLen < 4 || pos + 8 + pairLen > pairEnd) break;
            var idBytes = new byte[4];
            fs.ReadExactly(idBytes, 0, 4);
            uint id = BitConverter.ToUInt32(idBytes, 0);
            if (id == 0x7109871a || id == 0xf05368c0)
            {
                AddScheme(id == 0x7109871a ? "v2" : "v3");
                var vLen = (int)(pairLen - 4);
                if (vLen > 0 && vLen < 8 * 1024 * 1024)
                {
                    var val = new byte[vLen];
                    fs.ReadExactly(val, 0, vLen);
                    ParseSigningBlockCerts(val);
                }
            }
            long next = pos + 8 + pairLen;
            if (next <= pos) break;
            fs.Seek(next, SeekOrigin.Begin);
            pos = next;
        }
    }

    private void AddScheme(string s)
    {
        if (!Schemes.Contains(s)) Schemes.Add(s);
    }

    /// <summary>从 APK Signing Block 的 v2/v3 签名值里提取证书（v2-only 的包只有这里能拿到）。</summary>
    private void ParseSigningBlockCerts(byte[] v)
    {
        try
        {
            int p = 0;
            int blockLen = (int)U32(v, ref p);
            if (blockLen <= 0) return;
            int signerLen = (int)U32(v, ref p);
            if (signerLen <= 0) return;
            int signedLen = (int)U32(v, ref p);
            if (signedLen <= 0) return;
            int digestsLen = (int)U32(v, ref p);
            p += digestsLen;
            int certsLen = (int)U32(v, ref p);
            int end = Math.Min(v.Length, p + certsLen);
            while (p + 4 <= end && string.IsNullOrEmpty(SignerSubject))
            {
                int certLen = (int)U32(v, ref p);
                if (certLen <= 0 || p + certLen > Math.Min(v.Length, end)) break;
                try
                {
                    var der = new byte[certLen];
                    Array.Copy(v, p, der, 0, certLen);
                    var cert = new X509Certificate2(der);
                    SignerSubject = cert.Subject;
                    SignerIssuer = cert.Issuer;
                    SignerSha256 = cert.GetCertHashString(HashAlgorithmName.SHA256);
                    SignerValid = cert.NotBefore.ToString("yyyy-MM-dd") + " ~ " + cert.NotAfter.ToString("yyyy-MM-dd");
                }
                catch { }
                p += certLen;
            }
        }
        catch { }
    }

    private static uint U32(byte[] d, ref int p)
    {
        if (p + 4 > d.Length) { p = d.Length; return 0; }
        uint v = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
        p += 4;
        return v;
    }

    // ---------- 报告 ----------

    public string OverviewReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("文件      " + FileName);
        sb.AppendLine("大小      " + SizeText(FileSize));
        sb.AppendLine("条目      " + EntryCount);
        sb.AppendLine("MD5       " + Md5);
        sb.AppendLine("SHA-1     " + Sha1);
        sb.AppendLine("SHA-256   " + Sha256);
        sb.AppendLine();
        sb.AppendLine("包名      " + Dash(Package));
        sb.AppendLine("版本      " + Dash(VersionName) + "   (versionCode " + Dash(VersionCode) + ")");
        sb.AppendLine("SDK       minSdk " + Dash(MinSdk) + " / targetSdk " + Dash(TargetSdk));
        sb.AppendLine("标签      " + Dash(AppLabel) + (AppDebuggable == "true" ? "   [debuggable]" : ""));
        sb.AppendLine("签名方案  " + (Schemes.Count == 0 ? (V1SigFiles.Count > 0 ? "v1" : "未发现") : "v1 + " + string.Join(" + ", Schemes)));
        sb.AppendLine("ABI       " + (Abis.Count == 0 ? "—" : string.Join(", ", Abis)));
        sb.AppendLine("屏幕密度  " + (Densities.Count == 0 ? "—" : string.Join(", ", Densities)));
        sb.AppendLine("权限      " + Permissions.Count + " 项");
        sb.AppendLine("组件      Activity " + Activities.Count + " / Service " + Services.Count +
                      " / Receiver " + Receivers.Count + " / Provider " + Providers.Count);
        sb.AppendLine();
        sb.AppendLine("DEX 文件  " + DexFiles.Count + " 个");
        foreach (var d in DexFiles) sb.AppendLine("  " + SizeText(d.Size).PadLeft(10) + "  " + d.Name);
        sb.AppendLine();
        sb.AppendLine("最大的条目：");
        foreach (var e in Largest) sb.AppendLine("  " + SizeText(e.Size).PadLeft(10) + "  " + e.Name);
        if (!string.IsNullOrWhiteSpace(ParseError))
        {
            sb.AppendLine();
            sb.AppendLine("解析警告：");
            sb.AppendLine(ParseError.TrimEnd());
        }
        return sb.ToString();
    }

    public string ComponentsReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("包名：" + Dash(Package));
        sb.AppendLine("application：label=" + Dash(AppLabel) + "  debuggable=" + Dash(AppDebuggable) +
                      "  allowBackup=" + Dash(AppAllowBackup));
        sb.AppendLine();
        AppendList(sb, "权限（" + Permissions.Count + "）", Permissions);
        AppendList(sb, "Activity（" + Activities.Count + "）", Activities);
        AppendList(sb, "Service（" + Services.Count + "）", Services);
        AppendList(sb, "Receiver（" + Receivers.Count + "）", Receivers);
        AppendList(sb, "Provider（" + Providers.Count + "）", Providers);
        return sb.ToString();
    }

    public string SignatureReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("APK：" + FileName);
        sb.AppendLine("SHA-256：" + Sha256);
        sb.AppendLine();
        sb.AppendLine("v1 签名文件（META-INF）：");
        if (V1SigFiles.Count == 0) sb.AppendLine("  （无）");
        foreach (var f in V1SigFiles) sb.AppendLine("  " + f);
        sb.AppendLine();
        sb.AppendLine("APK Signing Block：" + (Schemes.Count == 0 ? "未发现 v2/v3" : string.Join(" + ", Schemes)));
        sb.AppendLine();
        if (!string.IsNullOrEmpty(SignerSubject))
        {
            sb.AppendLine("证书 Subject：" + SignerSubject);
            sb.AppendLine("证书 Issuer ：" + SignerIssuer);
            sb.AppendLine("有效期      ：" + SignerValid);
            sb.AppendLine("SHA-256     ：" + SignerSha256);
        }
        else
        {
            sb.AppendLine("证书：未能解析（可能是拆分包 / 非常规签名）");
        }
        sb.AppendLine();
        sb.AppendLine("提示：证书指纹相同 = 同一个签名密钥；改包后必须用同一密钥重签才能覆盖安装。");
        return sb.ToString();
    }

    public string DexReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("DEX 概览（每个 classes*.dex 的头部统计）：");
        if (DexStats.Count == 0) { sb.AppendLine("（没有可解析的 DEX，可能被加固 / 加密）"); return sb.ToString(); }
        foreach (var d in DexStats)
        {
            sb.AppendLine();
            sb.AppendLine(d.Name + "   " + SizeText(d.Size) + "   dex " + d.Version + ".0");
            sb.AppendLine("  strings " + d.StringIds + "   types " + d.TypeIds + "   protos " + d.ProtoIds);
            sb.AppendLine("  fields  " + d.FieldIds + "   methods " + d.MethodIds + "   classes " + d.ClassDefs);
        }
        var totalMethods = DexStats.Sum(x => (long)x.MethodIds);
        var totalClasses = DexStats.Sum(x => (long)x.ClassDefs);
        sb.AppendLine();
        sb.AppendLine("合计：class " + totalClasses + " / method " + totalMethods + " / string " + DexStrings.Count);
        if (DexStrings.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("（只有壳 DEX：字符串池为空或被加密，常见于加固/商业保护）");
        }
        return sb.ToString();
    }

    public string ResourceStringsText()
    {
        if (ResourceStrings.Count == 0) return "没解析到资源字符串（resources.arsc 可能被加密或不存在）。";
        var sb = new StringBuilder();
        sb.AppendLine("resources.arsc 全局字符串池：" + ResourceStrings.Count + " 条");
        sb.AppendLine();
        foreach (var s in ResourceStrings) sb.AppendLine(s);
        return sb.ToString();
    }

    public string DexStringsText(string filter, int limit = 60000)
    {
        if (DexStrings.Count == 0) return "没有可解析的 DEX 字符串（可能被加固/加密）。";
        var list = string.IsNullOrWhiteSpace(filter)
            ? DexStrings
            : DexStrings.Where(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("DEX 字符串：" + DexStrings.Count + " 条" +
                      (string.IsNullOrWhiteSpace(filter) ? "" : "，匹配 " + list.Count + " 条"));
        sb.AppendLine();
        int n = 0;
        foreach (var s in list)
        {
            if (n++ >= limit) { sb.AppendLine("…（已截断）"); break; }
            sb.AppendLine(s);
        }
        return sb.ToString();
    }

    public string DexStringsForExport()
    {
        var sb = new StringBuilder();
        foreach (var s in DexStrings) sb.AppendLine(s);
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string title, List<string> items)
    {
        sb.AppendLine("=== " + title + " ===");
        if (items.Count == 0) sb.AppendLine("  （无）");
        foreach (var i in items) sb.AppendLine("  " + i);
        sb.AppendLine();
    }

    private static string Dash(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    public static string SizeText(long bytes)
    {
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString("0.##") + " " + units[u];
    }

    private static string Hex(byte[]? b) => b == null ? "" : Convert.ToHexString(b);

    /// <summary>
    /// 读 zip 条目，但带硬上限：以前 manifest / arsc 是直接 CopyTo(MemoryStream)，
    /// 一个几十 KB 的 zip bomb 就能把内存吃光；dex 那处本来就有 96MB 上限，这两处漏了。
    /// </summary>
    private static byte[] ReadEntryCapped(Stream s, long declaredLength, int max, string name)
    {
        if (declaredLength > max)
            throw new InvalidDataException($"{name} 声明大小超过 {max / 1024 / 1024}MB 上限，已跳过");

        using var ms = new MemoryStream(declaredLength is > 0 and < int.MaxValue ? (int)declaredLength : 64 * 1024);
        var buf = new byte[64 * 1024];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > max)
                throw new InvalidDataException($"{name} 解压后超过 {max / 1024 / 1024}MB 上限，已跳过");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}


/// <summary>resources.arsc 全局字符串池提取。</summary>
internal static class Arsc
{
    public static List<string> ExtractStrings(byte[] data)
    {
        var result = new List<string>();
        if (data.Length < 12) return result;
        int type = AxmlReader.U16(data, 0);
        if (type != 0x0002) return result;
        int headerSize = AxmlReader.U16(data, 2);
        int total = (int)AxmlReader.U32(data, 4);
        if (total <= 0 || total > data.Length) total = data.Length;
        int p = headerSize;
        while (p + 8 <= total)
        {
            int ct = AxmlReader.U16(data, p);
            int csz = (int)AxmlReader.U32(data, p + 4);
            if (csz <= 0) break;
            if (ct == 0x0001)
            {
                result = AxmlReader.ReadStringPool(data, p);
                break;
            }
            p += csz;
        }
        return result;
    }
}

/// <summary>DEX 头部与字符串池读取。</summary>
internal static class Dex
{
    public static ApkAnalysis.DexStat ReadStat(string name, byte[] d)
    {
        var s = new ApkAnalysis.DexStat
        {
            Name = name,
            Size = d.Length,
        };
        if (d.Length < 0x70) return s;
        s.Version = int.TryParse(Encoding.ASCII.GetString(d, 4, 3), out var v) ? v : 0;
        s.StringIds = (int)AxmlReader.U32(d, 0x38);
        s.TypeIds = (int)AxmlReader.U32(d, 0x40);
        s.ProtoIds = (int)AxmlReader.U32(d, 0x48);
        s.FieldIds = (int)AxmlReader.U32(d, 0x50);
        s.MethodIds = (int)AxmlReader.U32(d, 0x58);
        s.ClassDefs = (int)AxmlReader.U32(d, 0x60);
        return s;
    }

    public static List<string> ReadStrings(byte[] d)
    {
        var list = new List<string>();
        if (d.Length < 0x70) return list;
        int count = (int)AxmlReader.U32(d, 0x38);
        int off = (int)AxmlReader.U32(d, 0x3C);
        if (count <= 0 || count > 2_000_000 || off <= 0 || off >= d.Length) return list;
        for (int i = 0; i < count; i++)
        {
            int idOff = off + i * 4;
            if (idOff + 4 > d.Length) break;
            int dataOff = (int)AxmlReader.U32(d, idOff);
            if (dataOff <= 0 || dataOff >= d.Length) continue;
            list.Add(ReadMutf8(d, dataOff));
        }
        return list;
    }

    private static string ReadMutf8(byte[] d, int p)
    {
        // 跳过 uleb128 的 utf16 长度
        while (p < d.Length)
        {
            byte b = d[p++];
            if ((b & 0x80) == 0) break;
        }
        var sb = new StringBuilder();
        while (p < d.Length && d[p] != 0)
        {
            int b = d[p++];
            if (b < 0x80) sb.Append((char)b);
            else if ((b & 0xE0) == 0xC0)
            {
                if (p >= d.Length) break;
                int b2 = d[p++];
                sb.Append((char)(((b & 0x1F) << 6) | (b2 & 0x3F)));
            }
            else if ((b & 0xF0) == 0xE0)
            {
                if (p + 1 >= d.Length) break;
                int b2 = d[p++], b3 = d[p++];
                sb.Append((char)(((b & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F)));
            }
            else if ((b & 0xF8) == 0xF0)
            {
                if (p + 2 >= d.Length) break;
                int b2 = d[p++], b3 = d[p++], b4 = d[p++];
                int cp = ((b & 0x07) << 18) | ((b2 & 0x3F) << 12) | ((b3 & 0x3F) << 6) | (b4 & 0x3F);
                cp -= 0x10000;
                sb.Append((char)(0xD800 + (cp >> 10)));
                sb.Append((char)(0xDC00 + (cp & 0x3FF)));
            }
            else sb.Append('?');
        }
        return sb.ToString();
    }
}
