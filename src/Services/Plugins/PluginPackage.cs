using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using DashFile.PluginApi;

namespace DashFile.Services.Plugins;

/// <summary>
/// .dfp 插件包 = zip：plugin.json + 入口 DLL（可带依赖 DLL/说明/许可证）。
/// 支持根目录直接放文件，或只包一层以插件 id 命名的目录。
/// </summary>
internal static class PluginPackage
{
    public const string Extension = ".dfp";
    private const long MaxTotalBytes = 256L * 1024 * 1024;
    private const long MaxEntryBytes = 128L * 1024 * 1024;

    public static bool IsPackage(string path)
        => !string.IsNullOrWhiteSpace(path) &&
           string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    public static string Extract(string packagePath, string tempDirectory)
    {
        if (!IsPackage(packagePath))
            throw new InvalidOperationException("只接受 .dfp 插件包");
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("找不到插件包", packagePath);

        Directory.CreateDirectory(tempDirectory);
        long total = 0;
        using (var zip = ZipFile.OpenRead(packagePath))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // 目录
                var name = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (name.Length == 0 || name.Split('/').Any(s => s == ".." || s.Length == 0))
                    throw new InvalidOperationException("插件包包含非法路径：" + entry.FullName);

                total += entry.Length;
                if (total > MaxTotalBytes || entry.Length > MaxEntryBytes)
                    throw new InvalidOperationException("插件包过大");

                var dest = Path.GetFullPath(Path.Combine(tempDirectory, name));
                var root = Path.GetFullPath(tempDirectory) + Path.DirectorySeparatorChar;
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("插件包路径越界：" + entry.FullName);

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        var manifestPath = Path.Combine(tempDirectory, PluginApiInfo.ManifestFileName);
        if (File.Exists(manifestPath)) return tempDirectory;

        var dirs = Directory.GetDirectories(tempDirectory);
        if (dirs.Length == 1 &&
            File.Exists(Path.Combine(dirs[0], PluginApiInfo.ManifestFileName)))
            return dirs[0];

        throw new InvalidOperationException("插件包根目录缺少 plugin.json");
    }

    public static PluginManifest ReadManifest(string packageRoot)
    {
        var path = Path.Combine(packageRoot, PluginApiInfo.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidOperationException("plugin.json 缺少 id");
        if (!IsSafeId(manifest.Id))
            throw new InvalidOperationException("plugin.json 的 id 不合法：" + manifest.Id);

        var assembly = manifest.Assembly;
        if (string.IsNullOrWhiteSpace(assembly))
        {
            var dlls = Directory.GetFiles(packageRoot, "*.dll", SearchOption.TopDirectoryOnly);
            assembly = Path.GetFileName(dlls.Length == 1 ? dlls[0] : "");
        }
        var assemblyFull = Path.GetFullPath(Path.Combine(packageRoot, assembly));
        var rootFull = Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(assembly) || !assemblyFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(assemblyFull))
            throw new InvalidOperationException("插件包缺少入口 DLL 或路径越界：" + assembly);
        manifest.Assembly = assembly;
        return manifest;
    }

    public static bool IsSafeId(string id)
        => !string.IsNullOrWhiteSpace(id) && id != "." && id != ".." &&
           id.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');
}
