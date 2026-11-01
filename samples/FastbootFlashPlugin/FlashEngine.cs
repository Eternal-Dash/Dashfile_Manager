using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FastbootFlashPlugin;

public sealed record Partition(string Name, long Size, bool? Logical = null, string? BlockPath = null)
{
    public string SizeText => Size > 0 ? FormatBytes(Size) : "大小未知";
    public string Kind => Logical == true ? "逻辑分区" : Logical == false ? "物理分区" : "类型未报告";
    public static string FormatBytes(long size) => size >= 1L << 30 ? $"{size / (double)(1L << 30):F2} GiB" : size >= 1L << 20 ? $"{size / (double)(1L << 20):F1} MiB" : $"{size:N0} B";
}
public sealed record DeviceDetails(DeviceTarget Target, string Product, string Slot, bool Userspace, bool? Unlocked, IReadOnlyList<Partition> Partitions);
public sealed record ImageInfo(string Path, long Length, long ExpandedSize, string Sha256);
public sealed record FlashStep(string Partition, ImageInfo Image, bool PackageUpdate = false);
public sealed record FlashPlan(DeviceTarget Target, string Product, bool ManualUnlock, bool AllowCritical, IReadOnlyList<FlashStep> Steps, string Notes);
public sealed record BackupEntry(string Name, string File, long Size, string Sha256, bool? Logical);
public sealed class BackupManifest
{
    public string Format { get; set; } = "DashFile.PartitionBackup/1";
    public string Product { get; set; } = "";
    public string Serial { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Coverage { get; set; } = "selected-enumerated-partitions";
    public bool Complete { get; set; }
    public string? Error { get; set; }
    public List<string> Requested { get; set; } = new();
    public List<BackupEntry> Entries { get; set; } = new();
}

public sealed class FlashEngine
{
    private readonly IToolRunner _tools;
    private readonly SemaphoreSlim _operation = new(1, 1);
    public event Action<string>? Log;
    public event Action<int, int, string>? Progress;
    public bool StopRequested { get; private set; }
    public bool IsWriting { get; private set; }
    public void RequestStop() => StopRequested = true;
    public FlashEngine(IToolRunner tools) => _tools = tools;
    private void Say(string s) => Log?.Invoke(s);
    public static string Name(string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$") || name is "." or "..")
            throw new InvalidDataException("分区名只允许字母、数字、下划线、点和短横线，不能包含路径或命令。 ");
        return name;
    }
    public static bool Critical(string name)
    {
        var n = Regex.Replace(Name(name).ToLowerInvariant(), "_[ab]$", "");
        return new[] { "userdata", "metadata", "persist", "frp", "modemst1", "modemst2", "fsg", "devinfo", "super", "gpt", "partition", "abl", "xbl", "xbl_config", "bootloader", "radio", "modem", "preloader", "lk", "tz", "hyp" }.Contains(n);
    }
    private Task<ToolResult> Call(DeviceTarget target, IEnumerable<string> args, CancellationToken ct, string? file = null, bool write = false)
    {
        if (string.IsNullOrWhiteSpace(target.Serial) || target.Serial.StartsWith('-') || target.Serial.Any(char.IsWhiteSpace))
            throw new InvalidDataException("必须指定明确的设备序列号。");
        var list = new[] { "-s", target.Serial }.Concat(args).ToArray();
        Say((target.Transport == Transport.Fastboot ? "fastboot " : "adb ") + string.Join(" ", list.Select(a => a.Contains(' ') ? '"' + a + '"' : a)));
        return _tools.RunAsync(target.Transport, list, file, write ? null : TimeSpan.FromSeconds(file != null || list.Contains("fetch") ? 3600 : 20), Say, ct);
    }
    private static void Require(ToolResult result, string action)
    {
        if (!result.Ok || Regex.IsMatch(result.Output, @"(?im)^\s*(?:FAILED|fastboot:\s*error:|adb:\s*error:|dd:.*(?:denied|error))"))
            throw new IOException(action + "失败：" + result.Output.Trim());
    }
    public async Task<IReadOnlyList<DeviceTarget>> DiscoverAsync(CancellationToken ct)
    {
        var targets = new List<DeviceTarget>();
        foreach (var kind in new[] { Transport.Fastboot, Transport.AdbRoot })
        {
            try
            {
                var r = await _tools.RunAsync(kind, new[] { "devices" }, null, TimeSpan.FromSeconds(12), null, ct);
                foreach (Match match in Regex.Matches(r.Output, kind == Transport.Fastboot ? @"(?m)^([^\s]+)\s+fastboot\b" : @"(?m)^([^\s]+)\s+device\b"))
                    targets.Add(new DeviceTarget(match.Groups[1].Value, kind));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Say(kind + " 检测超时。"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Say(kind + " 检测：" + ex.Message); }
        }
        return targets.Distinct().ToArray();
    }
    public static string Variable(string output, string key)
    {
        var m = Regex.Match(output, @"(?im)^\s*(?:\(bootloader\)\s*)?" + Regex.Escape(key) + @":\s*([^\r\n]*)");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
    private async Task<string> Var(DeviceTarget device, string key, CancellationToken ct)
    {
        var r = await Call(device, new[] { "getvar", key }, ct);
        return r.Ok ? Variable(r.Output, key) : "";
    }
    public static IReadOnlyList<Partition> ParsePartitions(string output)
    {
        var parts = new Dictionary<string, Partition>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(output, @"(?im)^\s*(?:\(bootloader\)\s*)?partition-size:([A-Za-z0-9][A-Za-z0-9_.-]*):\s*(0x[0-9a-f]+|[0-9a-f]+)\s*$"))
        {
            var name = Name(m.Groups[1].Value);
            var size = ParseSize(m.Groups[2].Value);
            if (size > 0) parts[name] = new Partition(name, size, ParseYesNo(Variable(output, "is-logical:" + name)));
        }
        return parts.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    }
    // AOSP partition-size is hexadecimal even when a vendor omits the 0x prefix.
    public static long ParseSize(string value)
        => long.TryParse(value.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out var size) && size > 0 ? size : 0;
    public static bool? ParseYesNo(string value) => value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value == "1" ? true : value.Equals("no", StringComparison.OrdinalIgnoreCase) || value == "0" ? false : null;

    public async Task<DeviceDetails> InspectAsync(DeviceTarget target, CancellationToken ct)
    {
        if (target.Transport == Transport.Fastboot)
        {
            var all = await Call(target, new[] { "getvar", "all" }, ct);
            var product = Variable(all.Output, "product");
            if (product.Length == 0) product = await Var(target, "product", ct);
            if (product.Length == 0) throw new IOException("设备未报告 product；请检查连接后重试。");
            var slot = Variable(all.Output, "current-slot");
            if (slot.Length == 0) slot = await Var(target, "current-slot", ct);
            var userspace = Variable(all.Output, "is-userspace");
            if (userspace.Length == 0) userspace = await Var(target, "is-userspace", ct);
            var unlocked = Variable(all.Output, "unlocked");
            if (unlocked.Length == 0) unlocked = await Var(target, "unlocked", ct);
            return new DeviceDetails(target, product, slot.TrimStart('_'), ParseYesNo(userspace) == true, ParseYesNo(unlocked), ParsePartitions(all.Output));
        }
        var id = await Call(target, new[] { "exec-out", "su", "-c", "id -u" }, ct);
        Require(id, "Root 检测");
        if (id.Output.Trim() != "0") throw new IOException("ADB 回读需要设备给 shell 授予 Root。");
        var productResult = await Call(target, new[] { "shell", "getprop", "ro.product.device" }, ct);
        Require(productResult, "设备识别");
        var slotResult = await Call(target, new[] { "shell", "getprop", "ro.boot.slot_suffix" }, ct);
        const string scan = "for d in /dev/block/by-name /dev/block/mapper; do for p in \"$d\"/*; do [ -b \"$p\" ] || continue; n=${p##*/}; s=$(blockdev --getsize64 \"$p\" 2>/dev/null); printf '%s|%s|%s\\n' \"$n\" \"$s\" \"$p\"; done; done";
        var list = await Call(target, new[] { "exec-out", "su", "-c", scan }, ct);
        Require(list, "分区枚举");
        var rows = new Dictionary<string, Partition>(StringComparer.Ordinal);
        foreach (var line in list.Output.Split('\n'))
        {
            var fields = line.Trim().Split('|');
            if (fields.Length != 3 || !Regex.IsMatch(fields[0], "^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$")) continue;
            var path = fields[2];
            if (path != "/dev/block/by-name/" + fields[0] && path != "/dev/block/mapper/" + fields[0]) continue;
            long.TryParse(fields[1], out var size);
            rows.TryAdd(fields[0], new Partition(fields[0], size, path.StartsWith("/dev/block/mapper/"), path));
        }
        if (rows.Count == 0) throw new IOException("未找到可见的 by-name / mapper 分区，设备可能不支持该回读方式。");
        return new DeviceDetails(target, productResult.Output.Trim(), slotResult.Output.Trim().TrimStart('_'), false, null, rows.Values.OrderBy(p => p.Name).ToArray());
    }
    public async Task RebootAsync(DeviceTarget target, string mode, CancellationToken ct)
    {
        if (mode is not ("bootloader" or "fastboot" or "system" or "recovery" or "edl"))
            throw new InvalidDataException("未知重启模式。");
        if (!await _operation.WaitAsync(0, ct)) throw new InvalidOperationException("设备任务尚未结束，不能切换模式。");
        try
        {
            using var operationLock = LockDeviceOperations();
            var args = mode switch
            {
                "system" => new[] { "reboot" },
                // 9008 / EDL 走厂商扩展指令，只有部分机型支持；失败时把原始回显带出去
                "edl" => new[] { "oem", "edl" },
                _ => new[] { "reboot", mode }
            };
            var result = await Call(target, args, ct);
            if (!result.Ok && mode == "edl")
                throw new InvalidOperationException("设备不支持进入 9008/EDL（部分机型需专用工具或组合键）。\n" + result.Output.Trim());
            Require(result, "重启");
        }
        finally { _operation.Release(); }
    }

    /// <summary>执行一条 fastboot 命令（不含可执行文件名）。参数按空格切分，支持双引号包裹。</summary>
    public async Task<string> RunCommandAsync(DeviceTarget target, string commandLine, CancellationToken ct)
    {
        var args = SplitArgs(commandLine);
        if (args.Count == 0) throw new InvalidDataException("命令不能为空。");
        if (!await _operation.WaitAsync(0, ct)) throw new InvalidOperationException("设备任务尚未结束，不能执行命令。");
        try
        {
            using var operationLock = LockDeviceOperations();
            Say("fastboot " + string.Join(' ', args));
            var result = await Call(target, args, ct);
            Require(result, "命令");
            return result.Output.Trim();
        }
        finally { _operation.Release(); }
    }

    /// <summary>擦除分区。已明确锁定的设备禁止；关键分区需要额外放行。</summary>
    public async Task EraseAsync(DeviceDetails details, IReadOnlyList<Partition> parts, bool allowCritical, CancellationToken ct)
    {
        if (parts.Count == 0) throw new InvalidDataException("请先勾选要擦除的分区。");
        if (details.Unlocked == false) throw new InvalidOperationException("设备已明确锁定，不能擦除分区。");
        if (!allowCritical && parts.Any(p => Critical(p.Name)))
            throw new InvalidOperationException("所选分区包含关键 / 数据分区，请先在「高级」中允许。");
        if (!await _operation.WaitAsync(0, ct)) throw new InvalidOperationException("设备任务尚未结束。");
        try
        {
            using var operationLock = LockDeviceOperations();
            for (var i = 0; i < parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (StopRequested) { Say("已停止：擦除未全部完成。"); break; }
                Progress?.Invoke(i, parts.Count, parts[i].Name);
                Say($"erase {parts[i].Name}");
                Require(await Call(details.Target, new[] { "erase", parts[i].Name }, ct), "擦除 " + parts[i].Name);
            }
        }
        finally { _operation.Release(); }
    }

    private static List<string> SplitArgs(string line)
    {
        var list = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (current.Length > 0) { list.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) list.Add(current.ToString());
        return list;
    }
    public static async Task<ImageInfo> InspectImageAsync(string file, CancellationToken ct)
    {
        file = Path.GetFullPath(file);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        if (stream.Length == 0) throw new InvalidDataException("镜像为空：" + file);
        long expanded = stream.Length;
        var header = new byte[28];
        if (stream.Length >= 28 && await stream.ReadAsync(header, ct) == 28 && BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xed26ff3a)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != 1) throw new InvalidDataException("不支持的 sparse 主版本。");
            var fileHeader = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            var chunkHeader = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
            var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            var blocks = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            var chunks = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
            if (fileHeader < 28 || chunkHeader < 12 || blockSize == 0 || blockSize % 4 != 0 || chunks == 0 || fileHeader > stream.Length)
                throw new InvalidDataException("损坏的 sparse 头。");
            expanded = checked((long)blockSize * blocks);
            stream.Position = fileHeader;
            ulong seen = 0;
            for (uint i = 0; i < chunks; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ch = new byte[12];
                await stream.ReadExactlyAsync(ch, ct);
                var type = BinaryPrimitives.ReadUInt16LittleEndian(ch);
                var count = BinaryPrimitives.ReadUInt32LittleEndian(ch.AsSpan(4));
                var total = BinaryPrimitives.ReadUInt32LittleEndian(ch.AsSpan(8));
                var payload = type switch { 0xcac1 => checked((long)count * blockSize), 0xcac2 => 4L, 0xcac3 => 0L, 0xcac4 => 4L, _ => throw new InvalidDataException("未知 sparse chunk。") };
                if (total != chunkHeader + payload || stream.Position - 12 + total > stream.Length) throw new InvalidDataException("sparse chunk 长度错误。");
                if (type == 0xcac4 && count != 0) throw new InvalidDataException("损坏的 CRC chunk。");
                seen += count;
                stream.Position += total - 12;
            }
            if (seen != blocks || stream.Position != stream.Length || expanded <= 0) throw new InvalidDataException("sparse 块数或文件尾错误。");
        }
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return new ImageInfo(file, stream.Length, expanded, hash);
    }
    private async Task<string> ResolvePartition(DeviceDetails device, string partition, string slot, CancellationToken ct)
    {
        Name(partition);
        if (slot is not ("current" or "a" or "b")) throw new InvalidDataException("目标槽位无效。");
        if (partition.EndsWith("_a", StringComparison.Ordinal) || partition.EndsWith("_b", StringComparison.Ordinal))
        {
            if (slot != "current" && !partition.EndsWith("_" + slot, StringComparison.Ordinal)) throw new InvalidDataException("分区后缀与目标槽位冲突：" + partition);
            return partition;
        }
        if (await Var(device.Target, "has-slot:" + partition, ct) == "yes")
        {
            var suffix = slot == "current" ? device.Slot : slot;
            if (suffix is not ("a" or "b")) throw new InvalidDataException("无法识别当前槽位，请明确选择 A 或 B。");
            return partition + "_" + suffix;
        }
        return partition;
    }
    private static void Writable(DeviceDetails device, bool manualUnlock)
    {
        if (device.Target.Transport != Transport.Fastboot) throw new InvalidOperationException("刷写必须选择 Fastboot 设备；ADB 仅提供回读。");
        if (device.Unlocked == false) throw new InvalidOperationException("设备报告 Bootloader 已锁定，拒绝刷写。");
        if (device.Unlocked == null && !manualUnlock) throw new InvalidOperationException("解锁状态未知。核实已解锁后，勾选手动确认再生成计划。");
    }
    private async Task CheckPartition(DeviceDetails device, string name, ImageInfo image, bool critical, CancellationToken ct)
    {
        if (Critical(name) && !critical) throw new InvalidOperationException("包含关键分区 " + name + "，需单独允许后重新生成计划。");
        var logical = ParseYesNo(await Var(device.Target, "is-logical:" + name, ct)) ?? device.Partitions.FirstOrDefault(p => p.Name == name)?.Logical;
        if (logical == true && !device.Userspace) throw new InvalidOperationException(name + " 是逻辑分区，请先进入 Fastbootd 再刷新设备。");
        var size = ParseSize(await Var(device.Target, "partition-size:" + name, ct));
        if (size <= 0) throw new InvalidOperationException("无法验证目标分区大小：" + name);
        if (image.ExpandedSize > size) throw new InvalidDataException($"{name} 镜像展开后 {Partition.FormatBytes(image.ExpandedSize)} 超过分区 {Partition.FormatBytes(size)}。");
    }
    public async Task<FlashPlan> PlanImagesAsync(DeviceDetails device, IEnumerable<(string Partition, string File)> files,
        string slot, bool manualUnlock, bool critical, CancellationToken ct)
    {
        // Re-read connection/lock/slot now; the UI snapshot is not trusted for a write.
        var expectedProduct = device.Product;
        device = await InspectAsync(device.Target, ct);
        if (device.Product != expectedProduct) throw new InvalidOperationException("设备机型已变化，请重新读取分区。");
        Writable(device, manualUnlock);
        var steps = new List<FlashStep>();
        foreach (var (partition, file) in files)
        {
            var target = await ResolvePartition(device, partition, slot, ct);
            if (steps.Any(s => s.Partition == target)) throw new InvalidDataException("重复目标分区：" + target);
            Say("校验镜像 " + Path.GetFileName(file));
            var image = await InspectImageAsync(file, ct);
            await CheckPartition(device, target, image, critical, ct);
            steps.Add(new FlashStep(target, image));
        }
        if (steps.Count == 0) throw new InvalidDataException("没有镜像任务。");
        return new FlashPlan(device.Target, device.Product, manualUnlock, critical, steps, "逐项写入，出错即停；不自动擦除、不关闭 AVB、不切换活动槽、不重启系统。");
    }
    public static string SafeChild(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') || relative.Split('/').Any(s => s is ".." or "."))
            throw new InvalidDataException("包内路径不安全：" + relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("包内路径越界。");
        return path;
    }
    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.Format != "DashFile.PartitionBackup/1" || !manifest.Complete || manifest.Entries.Count is 0 or > 1024 || manifest.Requested.Count != manifest.Entries.Count)
            throw new InvalidDataException("备份清单不完整，不能用于整包恢复。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in manifest.Entries)
            if (!names.Add(Name(e.Name)) || e.File != e.Name + ".img" || e.Size <= 0 || !Regex.IsMatch(e.Sha256, "^[A-Fa-f0-9]{64}$"))
                throw new InvalidDataException("备份清单含重复分区或无效条目。");
        if (!names.SetEquals(manifest.Requested) || manifest.Requested.Distinct(StringComparer.Ordinal).Count() != manifest.Requested.Count)
            throw new InvalidDataException("备份条目与请求分区不一致。");
    }
    public async Task<FlashPlan> PlanFolderAsync(DeviceDetails device, string folder, string slot, bool unlock, bool critical, CancellationToken ct)
    {
        folder = Path.GetFullPath(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("请使用实际镜像目录，不使用目录链接。");
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, ct)) ?? throw new InvalidDataException("备份清单为空。");
            ValidateManifest(manifest);
            if (manifest.Product != device.Product) throw new InvalidDataException("备份机型与当前设备不一致。");
            if (manifest.Serial != device.Target.Serial) throw new InvalidDataException("备份来自另一台设备，禁止直接批量恢复；请使用该设备原始备份。");
            var inputs = new List<(string, string)>();
            foreach (var entry in manifest.Entries)
            {
                Name(entry.Name);
                var file = SafeChild(folder, entry.File);
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("镜像不能为链接。");
                var image = await InspectImageAsync(file, ct);
                if (image.Length != entry.Size || !image.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份镜像校验失败：" + entry.Name);
                inputs.Add((entry.Name, file));
            }
            return await PlanImagesAsync(device, inputs, slot, unlock, critical, ct);
        }
        var files = Directory.GetFiles(folder, "*.img", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Any(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)) throw new InvalidDataException("镜像不能为链接。");
        return await PlanImagesAsync(device, files.Select(file => (Name(Path.GetFileNameWithoutExtension(file)), file)), slot, unlock, critical, ct);
    }
    public static string ValidateUpdateZip(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("包内条目过多。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            SafeChild(Path.GetTempPath(), entry.FullName);
            if (!names.Add(entry.FullName)) throw new InvalidDataException("包内文件重名。");
        }
        var info = zip.GetEntry("android-info.txt") ?? throw new InvalidDataException("需要 AOSP Fastboot update ZIP（根目录含 android-info.txt）；不支持直接刷 OTA/payload.bin/厂商脚本包。");
        if (info.Length > 65536) throw new InvalidDataException("android-info.txt 过大。");
        using var reader = new StreamReader(info.Open());
        var notes = reader.ReadToEnd();
        if (!zip.Entries.Any(e => e.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("更新包不含镜像。");
        if (zip.GetEntry("fastboot-info.txt") is { } plan)
        {
            if (plan.Length > 65536) throw new InvalidDataException("fastboot-info.txt 过大。");
            using var pr = new StreamReader(plan.Open());
            var script = pr.ReadToEnd();
            foreach (var line in script.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
            {
                var command = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (command is not ("version" or "flash" or "reboot" or "update-super" or "if-wipe"))
                    throw new InvalidDataException("更新包含不允许的指令：" + command);
                if (command == "reboot" && line != "reboot fastboot" && line != "reboot bootloader") throw new InvalidDataException("更新包会重启系统，无法保持预览后的写入会话。");
            }
            notes += "\nfastboot-info.txt:\n" + script;
        }
        notes += "\n包内镜像：\n" + string.Join("\n", zip.Entries.Where(e => e.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)).Select(e => e.FullName + " · " + Partition.FormatBytes(e.Length)));
        return notes;
    }
    public async Task<FlashPlan> PlanUpdateAsync(DeviceDetails device, string file, bool unlock, bool critical, CancellationToken ct)
    {
        var expectedProduct = device.Product;
        device = await InspectAsync(device.Target, ct);
        if (device.Product != expectedProduct) throw new InvalidOperationException("设备机型已变化，请重新读取分区。");
        Writable(device, unlock);
        var notes = ValidateUpdateZip(file);
        if (!critical) throw new InvalidOperationException("原生整包可能重建动态分区或包含关键固件，请核对包内计划并允许关键分区后重试。");
        var image = await InspectImageAsync(file, ct);
        return new FlashPlan(device.Target, device.Product, unlock, critical, new[] { new FlashStep("update.zip", image, true) },
            "原生 fastboot --skip-reboot update；不添加 -w / force / 关闭校验参数。包可能重建动态分区布局。\n" + notes);
    }
    private static FileStream LockDeviceOperations()
    {
        try { return new FileStream(Path.Combine(Path.GetTempPath(), "DashFile.FastbootFlash.operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("另一个刷写实例仍在操作设备，请等待其结束。"); }
    }
    public async Task ExecuteAsync(FlashPlan plan)
    {
        if (!await _operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在执行。");
        StopRequested = false;
        try
        {
            using var operationLock = LockDeviceOperations();
            var current = await InspectAsync(plan.Target, CancellationToken.None);
            Writable(current, plan.ManualUnlock);
            if (current.Product != plan.Product) throw new InvalidOperationException("设备机型已变化，请重新生成计划。");
            // Validate every image before any write; hold read-only handles to deny file replacement/writes on Windows.
            var held = new List<FileStream>();
            try
            {
                foreach (var step in plan.Steps)
                {
                    held.Add(new FileStream(step.Image.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    var check = await InspectImageAsync(step.Image.Path, CancellationToken.None);
                    if (check != step.Image) throw new InvalidDataException("镜像在预览后发生变化，请重新生成计划。");
                    if (step.PackageUpdate) ValidateUpdateZip(step.Image.Path);
                    else await CheckPartition(current, step.Partition, check, plan.AllowCritical, CancellationToken.None);
                }
                for (var i = 0; i < plan.Steps.Count; i++)
                {
                    if (StopRequested) throw new OperationCanceledException("已停止后续任务；已完成的刷写不会回滚。");
                    var step = plan.Steps[i];
                    Progress?.Invoke(i, plan.Steps.Count, step.Partition);
                    IsWriting = true;
                    ToolResult result;
                    try
                    {
                        result = await Call(plan.Target, step.PackageUpdate ? new[] { "--skip-reboot", "update", step.Image.Path } : new[] { "flash", step.Partition, step.Image.Path }, CancellationToken.None, write: true);
                    }
                    finally { IsWriting = false; }
                    Require(result, "写入 " + step.Partition);
                    Progress?.Invoke(i + 1, plan.Steps.Count, step.Partition);
                }
            }
            finally { foreach (var file in held) file.Dispose(); }
        }
        finally { IsWriting = false; _operation.Release(); }
    }
    public async Task<string> BackupAsync(DeviceDetails details, IReadOnlyList<Partition> partitions, string outputRoot, bool full, bool makeZip, CancellationToken ct)
    {
        if (partitions.Count == 0) throw new InvalidDataException("请先枚举并选择分区。");
        if (!await _operation.WaitAsync(0, ct)) throw new InvalidOperationException("已有操作正在执行。");
        StopRequested = false;
        string? folder = null;
        var manifest = new BackupManifest { Product = details.Product, Serial = details.Target.Serial, Requested = partitions.Select(p => p.Name).ToList(), Coverage = full ? "all-enumerated-partitions-not-a-vendor-ROM" : "selected-enumerated-partitions" };
        try
        {
            using var operationLock = LockDeviceOperations();
            var current = await InspectAsync(details.Target, ct);
            if (current.Product != details.Product) throw new IOException("设备机型已变化。");
            var expected = partitions.Aggregate(0L, (sum, p) => checked(sum + p.Size));
            if (partitions.Any(p => p.Size <= 0)) throw new InvalidOperationException("有分区大小未知，请刷新/更换回读方式，不能把未知长度结果当作完整备份。");
            Directory.CreateDirectory(outputRoot);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputRoot))!);
            if (drive.AvailableFreeSpace < checked(expected * (makeZip ? 2 : 1) + 64 * 1024 * 1024)) throw new IOException("输出磁盘空间不足（ZIP 需要额外一份镜像空间）。");
            folder = Path.Combine(outputRoot, "DashFile-Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            for (var i = 0; i < partitions.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (StopRequested) throw new OperationCanceledException("已停止回读。");
                var part = partitions[i];
                Name(part.Name);
                var fresh = current.Partitions.FirstOrDefault(p => p.Name == part.Name);
                if (fresh == null || fresh.Size != part.Size || fresh.BlockPath != part.BlockPath) throw new IOException("分区表已变化，请刷新后重试：" + part.Name);
                var temp = Path.Combine(folder, part.Name + ".img.partial");
                Progress?.Invoke(i, partitions.Count, part.Name);
                ToolResult result;
                if (details.Target.Transport == Transport.Fastboot)
                    result = await Call(details.Target, new[] { "fetch", part.Name, temp }, ct);
                else
                {
                    if (part.BlockPath != "/dev/block/by-name/" + part.Name && part.BlockPath != "/dev/block/mapper/" + part.Name) throw new InvalidDataException("块设备路径不合法。");
                    result = await Call(details.Target, new[] { "exec-out", "su", "-c", "dd if=" + part.BlockPath + " bs=1048576 2>/dev/null" }, ct, temp);
                }
                Require(result, "回读 " + part.Name);
                if (!File.Exists(temp) || new FileInfo(temp).Length != part.Size) throw new IOException("回读长度不符：" + part.Name + "；保留 .partial 文件，未标为成功。");
                var image = await InspectImageAsync(temp, ct);
                var final = Path.Combine(folder, part.Name + ".img");
                File.Move(temp, final);
                manifest.Entries.Add(new BackupEntry(part.Name, part.Name + ".img", image.Length, image.Sha256, part.Logical));
                await WriteManifest(folder, manifest, ct);
                Progress?.Invoke(i + 1, partitions.Count, part.Name);
            }
            manifest.Complete = true;
            await WriteManifest(folder, manifest, ct);
            if (makeZip)
            {
                var archive = folder + ".dfbackup.zip";
                using (var file = new FileStream(archive + ".partial", FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                    foreach (var path in manifest.Entries.Select(e => Path.Combine(folder, e.File)).Append(Path.Combine(folder, "manifest.json")))
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.NoCompression);
                        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                        using var dest = entry.Open();
                        await src.CopyToAsync(dest, ct);
                    }
                File.Move(archive + ".partial", archive);
                Say("备份包：" + archive);
            }
            return folder;
        }
        catch (Exception ex)
        {
            manifest.Complete = false; manifest.Error = ex.Message;
            if (folder != null) await WriteManifest(folder, manifest, CancellationToken.None);
            throw;
        }
        finally { _operation.Release(); }
    }
    private static Task WriteManifest(string folder, BackupManifest manifest, CancellationToken ct)
        => File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);

    public static async Task<string> ExtractBackupAsync(string archive, string cacheRoot, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 2048 || zip.GetEntry("manifest.json") is not { } entry || entry.Length > 1024 * 1024) throw new InvalidDataException("不是 DashFile 备份 ZIP。");
        using var input = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(input, cancellationToken: ct) ?? throw new InvalidDataException("备份清单无效。");
        ValidateManifest(manifest);
        var folder = Path.Combine(cacheRoot, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var required = manifest.Entries.Select(e => e.File).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
        var total = zip.Entries.Aggregate(0L, (sum, e) => checked(sum + e.Length));
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace < total + 64 * 1024 * 1024) throw new IOException("缓存磁盘空间不足。");
        foreach (var item in zip.Entries)
        {
            var file = SafeChild(folder, item.FullName);
            if (!names.Add(item.FullName) || !required.Contains(item.FullName) || item.FullName.Contains('/')) throw new InvalidDataException("备份 ZIP 有重名/多余/嵌套文件。");
            using var src = item.Open();
            using var dest = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await src.CopyToAsync(dest, ct);
        }
        if (names.Count != required.Count) throw new InvalidDataException("备份 ZIP 缺少文件。");
        return folder;
    }
}
