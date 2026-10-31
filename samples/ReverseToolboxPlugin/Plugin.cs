using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DashFile.PluginApi;
using DashFile.Services.OpenWith;

namespace ReverseToolboxPlugin;

/// <summary>
/// 逆向工具箱：只读分析 APK（Manifest / 签名 / DEX / 字符串 / 哈希）。
/// 参考 MT 管理器 / NP 管理器 / apktool / jadx 的常用能力，但全部在本机解析，不执行 APK 代码。
/// </summary>
public sealed class ReverseToolboxPlugin : PluginBase
{
    private const string IconSearch = "M7,2.5 a4.5,4.5 0 1,0 0,9 a4.5,4.5 0 1,0 0,-9 M10.5,10.5 l3,3";
    private const string IconList   = "M3,4 h10 M3,8 h10 M3,12 h10";
    private const string IconShield = "M8,2 l5,2.2 v3.6 l-5,6 l-5,-6 v-3.6 z M5.8,7.8 l2,2 l3.4,-3.4";
    private const string IconCode   = "M6,4 L2,8 L6,12 M10,4 L14,8 L10,12";
    private const string IconHash   = "M6,2.5 L4.5,13.5 M11,2.5 L9.5,13.5 M3,6 h10 M2.5,10 h10";
    private const string IconOpen   = "M2,4.3 h4.2 l1.4,1.6 h6.2 v7.3 h-11.8 z";
    private const string IconFile   = "M4.5,2.5 h5 l2.5,2.5 v8.5 h-7.5 z M9.5,2.5 v3 h2.5";

    public override PluginMetadata Metadata => new()
    {
        Id = "com.dashfile.reversetool",
        Name = "逆向工具箱",
        Version = "1.0.0",
        Author = "DashFile",
        Description = "APK 逆向分析：Manifest 解码、签名证书、DEX 结构/字符串、资源字符串、哈希、组件与权限（只读，不执行 APK）。",
    };

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        Host.AddPage(PluginPage.FromControl("toolbox", "逆向工具箱", BuildPage,
            "右键 APK / 应用，或用插件页分析本地安装包", "🧰"));

        Add("analyze", "逆向分析：查看这个 APK", IconSearch,
            t => ShowReportAsync(t, a => a.OverviewReport(), "逆向分析"));

        Add("components", "查看组件与权限…", IconList,
            t => ShowReportAsync(t, a => a.ComponentsReport(), "组件与权限"));

        Add("signature", "查看签名证书…", IconShield,
            t => ShowReportAsync(t, a => a.SignatureReport(), "签名信息"));

        Add("manifest", "导出解码后的 Manifest…", IconOpen,
            t => ExportTextAsync(t, "AndroidManifest", a => a.ManifestXml, ".xml"));

        Add("dex", "导出 DEX 文件…", IconCode,
            t => ExportDexAsync(t));

        Add("strings", "导出 DEX 字符串…", IconList,
            t => ExportTextAsync(t, "dex-strings", a => a.DexStringsForExport(), ".txt"));

        Add("resources", "导出资源字符串…", IconList,
            t => ExportTextAsync(t, "resources-strings", a => a.ResourceStringsText(), ".txt"));

        Add("hash", "计算哈希（MD5 / SHA1 / SHA256）…", IconHash,
            t => ShowHashAsync(t), anyFile: true);

        return Task.CompletedTask;
    }

    private void Add(string id, string title, string icon, Func<PluginTarget, Task> run, bool anyFile = false)
    {
        Host.AddAction(new PluginAction
        {
            Id = id,
            Title = title,
            Icon = icon,
            Targets = new[] { PluginTargetKind.App, PluginTargetKind.File },
            CanExecute = target =>
            {
                if (target.Kind == PluginTargetKind.App) return true;
                if (target.Kind != PluginTargetKind.File) return false;
                return anyFile || target.Path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
            },
            ExecuteAsync = async (target, ct) =>
            {
                try { await run(target); }
                catch (Exception ex)
                {
                    await Host.Ui.ShowMessageAsync("逆向工具箱", "操作失败：" + ex.Message, ct);
                }
            },
        });
    }

    // ---------------- 动作实现 ----------------

    private async Task ShowReportAsync(PluginTarget target, Func<ApkAnalysis, string> section, string title)
    {
        var local = await FetchApkAsync(target, CancellationToken.None);
        try
        {
            Host.Ui.Toast("正在分析 " + Path.GetFileName(local) + "…");
            var analysis = await Task.Run(() => ApkAnalysis.Build(local));
            var text = section(analysis);
            var fullTitle = title + " · " + target.DisplayName;
            await Host.Ui.RunOnUiThreadAsync(() =>
            {
                var win = new ReportWindow(fullTitle, text,
                    path => SaveTextAsync(path + ".txt", text));
                win.Show();
            });
        }
        finally
        {
            TryDelete(local);
        }
    }

    private async Task ShowHashAsync(PluginTarget target)
    {
        var local = await FetchApkAsync(target, CancellationToken.None, allowAnyFile: true);
        try
        {
            var (md5, sha1, sha256, size) = await Task.Run(() => ComputeHashes(local));
            var sb = new StringBuilder();
            sb.AppendLine("文件    " + Path.GetFileName(local));
            sb.AppendLine("大小    " + ApkAnalysis.SizeText(size));
            sb.AppendLine();
            sb.AppendLine("MD5     " + md5);
            sb.AppendLine("SHA-1   " + sha1);
            sb.AppendLine("SHA-256 " + sha256);
            var text = sb.ToString();
            await Host.Ui.RunOnUiThreadAsync(() =>
            {
                var win = new ReportWindow("哈希 · " + target.DisplayName, text,
                    path => SaveTextAsync(path + ".txt", text));
                win.Show();
            });
        }
        finally
        {
            TryDelete(local);
        }
    }

    private async Task ExportTextAsync(PluginTarget target, string baseName, Func<ApkAnalysis, string> section, string ext)
    {
        var local = await FetchApkAsync(target, CancellationToken.None);
        try
        {
            Host.Ui.Toast("正在分析…");
            var analysis = await Task.Run(() => ApkAnalysis.Build(local));
            var text = section(analysis);
            if (string.IsNullOrWhiteSpace(text))
            {
                await Host.Ui.ShowMessageAsync("逆向工具箱", "没有可导出的内容（可能被加固/加密）。");
                return;
            }
            var name = Path.GetFileNameWithoutExtension(local);
            var save = await SaveTextAsync(name + "-" + baseName + ext, text);
            if (save) Host.Ui.Toast("已导出：" + name + "-" + baseName + ext);
        }
        finally
        {
            TryDelete(local);
        }
    }

    private async Task ExportDexAsync(PluginTarget target)
    {
        var local = await FetchApkAsync(target, CancellationToken.None);
        try
        {
            var dir = await Host.Ui.PickFolderAsync("选择保存 DEX 的文件夹");
            if (string.IsNullOrEmpty(dir)) return;
            var count = 0;
            using (var zip = ZipFile.OpenRead(local))
            {
                foreach (var e in zip.Entries)
                {
                    if (!e.Name.StartsWith("classes", StringComparison.OrdinalIgnoreCase) ||
                        !e.Name.EndsWith(".dex", StringComparison.OrdinalIgnoreCase)) continue;
                    var dst = Path.Combine(dir!, e.Name);
                    e.ExtractToFile(dst, true);
                    count++;
                }
            }
            Host.Ui.Toast(count == 0 ? "没有找到 classes*.dex" : "已导出 " + count + " 个 DEX 到 " + dir);
        }
        finally
        {
            TryDelete(local);
        }
    }

    private async Task<bool> SaveTextAsync(string suggested, string text)
    {
        try
        {
            var path = await Host.Ui.SaveFileAsync("保存分析结果", suggested, "*.*");
            if (string.IsNullOrWhiteSpace(path)) return false;
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            Host.Ui.Toast("已保存：" + Path.GetFileName(path));
            return true;
        }
        catch (Exception ex)
        {
            Host.Ui.Toast("保存失败：" + ex.Message);
            return false;
        }
    }

    private async Task<string> FetchApkAsync(PluginTarget target, CancellationToken ct, bool allowAnyFile = false)
    {
        string remote;
        if (target.Kind == PluginTargetKind.App)
        {
            var app = await Host.Apps.GetAsync(target.Package, ct);
            remote = app?.ApkPath ?? target.Extra ?? "";
            if (string.IsNullOrWhiteSpace(remote))
                throw new InvalidOperationException("取不到安装包路径（系统预置或已卸载）。");
        }
        else
        {
            remote = target.Path;
            if (!allowAnyFile && !remote.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("这个文件不是 .apk。");
        }

        var cache = Path.Combine(Host.DataDirectory, "cache");
        Directory.CreateDirectory(cache);
        var local = Path.Combine(cache, "apk-" + Guid.NewGuid().ToString("N")[..10] + ".apk");
        await Host.Device.DownloadAsync(remote, local, ct);
        return local;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static (string Md5, string Sha1, string Sha256, long Size) ComputeHashes(string path)
    {
        using var md5 = MD5.Create();
        using var sha1 = SHA1.Create();
        using var sha256 = SHA256.Create();
        using var fs = File.OpenRead(path);
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
        return (Convert.ToHexString(md5.Hash!), Convert.ToHexString(sha1.Hash!),
                Convert.ToHexString(sha256.Hash!), new FileInfo(path).Length);
    }

    // ---------------- 插件页 ----------------

    private Control BuildPage(IPluginHost host)
    {
        ApkAnalysis? current = null;
        string currentName = "";
        string currentLocal = "";
        string section = "overview";

        var status = new TextBlock
        {
            Text = "选择 APK，或用「分析当前选中」分析文件页里选中的安装包。只读取结构，不执行 APK。",
            TextWrapping = TextWrapping.Wrap,
        };
        status.Classes.Add("sub");

        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize = 12.5,
            Text = "",
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        output.Classes.Add("path");

        var filter = new TextBox { Watermark = "筛选字符串（回车生效）", Width = 260, IsVisible = false };
        filter.Classes.Add("path");
        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filterRow.Children.Add(new TextBlock { Text = "筛选", VerticalAlignment = VerticalAlignment.Center });
        filterRow.Children.Add(filter);
        filterRow.IsVisible = false;

        var tabButtons = new Dictionary<string, Button>();
        string[] keys = { "overview", "manifest", "signature", "dex", "res", "str" };
        string[] titles = { "概览", "Manifest", "签名", "DEX", "资源字符串", "DEX 字符串" };

        void Render()
        {
            if (current == null)
            {
                output.Text = "尚未选择 APK。";
                return;
            }
            output.Text = section switch
            {
                "overview" => current.OverviewReport(),
                "manifest" => string.IsNullOrWhiteSpace(current.ManifestXml)
                    ? "（没能解码 AndroidManifest.xml）" : current.ManifestXml,
                "signature" => current.SignatureReport(),
                "dex" => current.DexReport(),
                "res" => current.ResourceStringsText(),
                "str" => current.DexStringsText(filter.Text ?? ""),
                _ => "",
            };
            filter.IsVisible = section is "res" or "str";
            filterRow.IsVisible = filter.IsVisible;
            status.Text = currentName + " · " + ApkAnalysis.SizeText(current.FileSize) +
                          " · DEX " + current.DexFiles.Count + " · 权限 " + current.Permissions.Count;
            foreach (var kv in tabButtons) kv.Value.Classes.Set("on", kv.Key == section);
        }

        void Select(string key)
        {
            section = key;
            Render();
        }

        void Analyze(string local, string name)
        {
            try { if (!string.IsNullOrEmpty(currentLocal)) TryDelete(currentLocal); } catch { }
            currentLocal = local;
            currentName = name;
            current = null;
            status.Text = "正在分析 " + name + " …";
            _ = Task.Run(() =>
            {
                ApkAnalysis a;
                try { a = ApkAnalysis.Build(local); }
                catch (Exception ex)
                {
                    host.Ui.RunOnUiThread(() => status.Text = "分析失败：" + ex.Message);
                    return;
                }
                host.Ui.RunOnUiThread(() =>
                {
                    current = a;
                    if (section == "overview") section = "overview";
                    Render();
                });
            });
        }

        var pick = new Button { Content = "选择 APK…" };
        pick.Classes.Add("tool");
        pick.Click += async (_, _) =>
        {
            var path = await host.Ui.OpenFileAsync("选择要分析的 APK", "*.apk");
            if (!string.IsNullOrWhiteSpace(path)) Analyze(path!, Path.GetFileName(path!));
        };

        var useSelection = new Button { Content = "分析当前选中" };
        useSelection.Classes.Add("tool");
        useSelection.Click += async (_, _) =>
        {
            var target = host.Files.SelectedTarget;
            if (target == null || target.Kind != PluginTargetKind.File ||
                !target.Path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                host.Ui.Toast("请先在文件页选中一个 .apk");
                return;
            }
            var cache = Path.Combine(host.DataDirectory, "cache");
            Directory.CreateDirectory(cache);
            var local = Path.Combine(cache, "apk-" + Guid.NewGuid().ToString("N")[..10] + ".apk");
            status.Text = "正在从手机读取 " + Path.GetFileName(target.Path) + " …";
            try
            {
                await host.Device.DownloadAsync(target.Path, local, CancellationToken.None);
                Analyze(local, Path.GetFileName(target.Path));
            }
            catch (Exception ex)
            {
                status.Text = "读取失败：" + ex.Message;
            }
        };

        var save = new Button { Content = "保存当前结果…" };
        save.Classes.Add("tool");
        save.Click += async (_, _) =>
        {
            if (current == null) { host.Ui.Toast("还没有分析结果"); return; }
            var text = output.Text ?? "";
            await SaveTextAsync(currentName + "-" + section + ".txt", text);
        };

        filter.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Render();
                e.Handled = true;
            }
        };

        var tabBar = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var btn = new Button { Content = titles[i], Margin = new Thickness(0, 0, 8, 8) };
            btn.Classes.Add("stylechip");
            btn.Click += (_, _) => Select(key);
            tabButtons[key] = btn;
            tabBar.Children.Add(btn);
        }

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { pick, useSelection, save })
        {
            b.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(b);
        }

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(new TextBlock
        {
            Text = "逆向工具箱",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(head);
        top.Children.Add(tabBar);
        top.Children.Add(filterRow);
        top.Children.Add(status);

        var root = new DockPanel { Margin = new Thickness(16, 12) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(output);

        // 默认选中概览（此时还没有 APK，只显示提示）
        Select("overview");

        // 调试/自动化：直接分析一个本地 APK（不做任何写入，只读）
        try
        {
            var auto = Environment.GetEnvironmentVariable("DASHFILE_REVTOOL_APK");
            if (!string.IsNullOrWhiteSpace(auto) && File.Exists(auto))
                Analyze(auto!, Path.GetFileName(auto!));
        }
        catch { }
        return root;
    }
}
