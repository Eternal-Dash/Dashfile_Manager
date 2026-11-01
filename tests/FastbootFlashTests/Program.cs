using FastbootFlashPlugin;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Diagnostics;

if (args.FirstOrDefault() == "--fake-tool")
{
    if (args.Contains("sleep")) { await Task.Delay(30000); return 0; }
    if (args.Contains("args")) { Console.WriteLine(JsonSerializer.Serialize(args.Skip(2))); return 0; }
    var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    for (var i = 0; i < 300; i++) Console.Error.WriteLine("diagnostic " + i + new string('x', 300));
    await Console.OpenStandardOutput().WriteAsync(bytes); return args.Contains("fail") ? 7 : 0;
}
int failed = 0, passed = 0;
void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (ok) passed++; else failed++; }
async Task Reject(string name, Func<Task> task)
{
    try { await task(); Check(name, false); } catch (Exception ex) when (ex is not StackOverflowException) { Check(name, true); }
}
var root = Path.Combine(Path.GetTempPath(), "DashFile-FlashTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var ct = CancellationToken.None;
var fake = new FakeRunner(); var engine = new FlashEngine(fake); var target = new DeviceTarget("TEST-ONLY", Transport.Fastboot);
var file = Path.Combine(root, "boot image with spaces.img"); await File.WriteAllBytesAsync(file, Enumerable.Repeat((byte)0x7a, 256).ToArray());
var parts = FlashEngine.ParsePartitions(fake.All);
Check("getvar stderr-style partition enumeration", parts.Count == 2 && parts[0].Name == "boot_a" && parts[0].Size == 4096);
Check("hex size protocol without prefix", FlashEngine.ParseSize("1000") == 4096);
foreach (var bad in new[] { "../boot", "boot;erase", "--slot", "a/b", "", "boot a" }) await Reject("reject partition " + bad, () => Task.FromResult(FlashEngine.Name(bad)));
foreach (var bad in new[] { "../evil.img", "/absolute", "C:/evil", "a\\b", "a/../../b" }) await Reject("reject archive path " + bad, () => Task.FromResult(FlashEngine.SafeChild(root, bad)));
var details = await engine.InspectAsync(target, ct);
fake.Product = "different-product"; await Reject("changed product rejected before preview", () => engine.PlanImagesAsync(details, new[] { ("boot", file) }, "current", false, false, ct)); fake.Product = "test-product";
var plan = await engine.PlanImagesAsync(details, new[] { ("boot", file) }, "current", false, false, ct);
Check("slot resolution explicit boot_a", plan.Steps.Single().Partition == "boot_a");
Check("preview does not flash", fake.Writes == 0);
using (var crossInstance = new FileStream(Path.Combine(Path.GetTempPath(), "DashFile.FastbootFlash.operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
{
    await Reject("cross-instance lock blocks concurrent operation", () => engine.ExecuteAsync(plan));
    await Reject("cross-instance lock blocks reboot during operation", () => engine.RebootAsync(target, "system", ct));
}
await engine.ExecuteAsync(plan);
Check("exact target and separate path args", fake.Calls.Any(a => a.SequenceEqual(new[] { "-s", "TEST-ONLY", "flash", "boot_a", file })));
Check("write process has no cancellation or timeout", fake.WriteUninterruptible);
fake.Writes = 0;
await File.AppendAllTextAsync(file, "changed"); await Reject("changed image blocked before write", () => engine.ExecuteAsync(plan)); Check("changed image zero writes", fake.Writes == 0);
plan = await engine.PlanImagesAsync(details, new[] { ("boot", file) }, "current", false, false, ct);
fake.Unlocked = "no"; await Reject("locked device rejected even manual override", () => engine.PlanImagesAsync(details, new[] { ("boot", file) }, "current", true, false, ct));
fake.Unlocked = ""; await Reject("unknown lock needs explicit acknowledgement", () => engine.PlanImagesAsync(details, new[] { ("boot", file) }, "current", false, false, ct)); fake.Unlocked = "yes";
await Reject("critical partition opt-in", () => engine.PlanImagesAsync(details, new[] { ("userdata", file) }, "current", false, false, ct));
await Reject("conflicting explicit slot", () => engine.PlanImagesAsync(details, new[] { ("boot_a", file) }, "b", false, false, ct));
fake.Logical = true; await Reject("logical partition requires fastbootd", () => engine.PlanImagesAsync(details, new[] { ("boot", file) }, "current", false, false, ct)); fake.Logical = false;
fake.Size = 1; await Reject("oversize image rejected", () => engine.PlanImagesAsync(details, new[] { ("boot", file) }, "current", false, false, ct)); fake.Size = 4096;
var sparse = Path.Combine(root, "sparse.img");
var data = new byte[40]; BinaryPrimitives.WriteUInt32LittleEndian(data, 0xed26ff3a); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 28); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), 12); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 4096); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 10); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 0xcac3); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 10); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 12);
await File.WriteAllBytesAsync(sparse, data); Check("sparse expanded size", (await FlashEngine.InspectImageAsync(sparse, ct)).ExpandedSize == 40960);
await Reject("sparse expanded oversize rejected", () => engine.PlanImagesAsync(details, new[] { ("boot", sparse) }, "current", false, false, ct));
data[36] = 20; await File.WriteAllBytesAsync(sparse, data); await Reject("malformed sparse rejected", () => FlashEngine.InspectImageAsync(sparse, ct));
var both = await engine.PlanImagesAsync(details, new[] { ("boot_a", file), ("vbmeta_a", file) }, "current", false, false, ct);
fake.Writes = 0; fake.FailWrite = true; await Reject("write failure stops sequence", () => engine.ExecuteAsync(both)); Check("second write not started", fake.Writes == 1); fake.FailWrite = false;
fake.Writes = 0; fake.OnWrite = () => engine.RequestStop(); await Reject("stop waits current then stops next", () => engine.ExecuteAsync(both)); Check("one write completed before stop", fake.Writes == 1); fake.OnWrite = null;
fake.Writes = 0; fake.FailPartition = "vbmeta_a"; await Reject("whole plan rechecked before any write", () => engine.ExecuteAsync(both)); Check("preflight failure zero writes", fake.Writes == 0); fake.FailPartition = "";
string Zip(string name, params (string Name, string Text)[] entries) { var path = Path.Combine(root, name); using var z = ZipFile.Open(path, ZipArchiveMode.Create); foreach (var e in entries) { using var w = new StreamWriter(z.CreateEntry(e.Name).Open()); w.Write(e.Text); } return path; }
var zip = Zip("update.zip", ("android-info.txt", "require product=test-product"), ("boot.img", "image"));
await Reject("native update requires explicit critical opt-in", () => engine.PlanUpdateAsync(details, zip, false, false, ct));
var update = await engine.PlanUpdateAsync(details, zip, false, true, ct); fake.Calls.Clear(); await engine.ExecuteAsync(update);
Check("native update skip-reboot, no wipe/AVB bypass", fake.Calls.Any(a => a.SequenceEqual(new[] { "-s", "TEST-ONLY", "--skip-reboot", "update", zip })) && !fake.Calls.SelectMany(a => a).Any(a => a is "-w" or "--force" or "--disable-verity" or "--disable-verification"));
foreach (var bad in new[] { Zip("traversal.zip", ("android-info.txt", ""), ("../boot.img", "")), Zip("ota.zip", ("payload.bin", "")), Zip("duplicate.zip", ("android-info.txt", ""), ("boot.img", ""), ("BOOT.IMG", "")), Zip("erase.zip", ("android-info.txt", ""), ("boot.img", ""), ("fastboot-info.txt", "erase userdata")) }) await Reject("invalid update " + Path.GetFileName(bad), () => Task.FromResult(FlashEngine.ValidateUpdateZip(bad)));
var backup = await engine.BackupAsync(details, details.Partitions, Path.Combine(root, "backups"), true, true, ct);
var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(Path.Combine(backup, "manifest.json")))!;
Check("backup all exact lengths, manifest hashes", manifest.Complete && manifest.Entries.Count == 2 && manifest.Entries.All(e => e.Size == 4096 && e.Sha256.Length == 64));
Check("coverage truthful", manifest.Coverage == "all-enumerated-partitions-not-a-vendor-ROM");
var extracted = await FlashEngine.ExtractBackupAsync(backup + ".dfbackup.zip", Path.Combine(root, "staging"), ct);
await Reject("backup restore rejects different device", () => engine.PlanFolderAsync(details with { Target = new DeviceTarget("OTHER-DEVICE", Transport.Fastboot) }, extracted, "current", false, false, ct));
Check("own archive restorable", (await engine.PlanFolderAsync(details, extracted, "current", false, false, ct)).Steps.Count == 2);
await File.AppendAllTextAsync(Path.Combine(extracted, "boot_a.img"), "tamper"); await Reject("restore rejects hash mismatch", () => engine.PlanFolderAsync(details, extracted, "current", false, false, ct));
fake.ShortRead = true; await Reject("short read fails backup", () => engine.BackupAsync(details, details.Partitions, Path.Combine(root, "incomplete"), true, false, ct));
var partial = Directory.GetDirectories(Path.Combine(root, "incomplete")).Single(); var partialManifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(Path.Combine(partial, "manifest.json")))!;
Check("failed backup remains incomplete", !partialManifest.Complete && partialManifest.Error != null && File.Exists(Path.Combine(partial, "boot_a.img.partial")));
await Reject("incomplete backup cannot restore", () => engine.PlanFolderAsync(details, partial, "current", false, false, ct)); fake.ShortRead = false;
var adbDetails = await engine.InspectAsync(new DeviceTarget("ROOT-TEST", Transport.AdbRoot), ct); Check("root partition discovery", adbDetails.Partitions.Count == 1);
var adbBackup = await engine.BackupAsync(adbDetails, adbDetails.Partitions, Path.Combine(root, "adb"), false, false, ct);
Check("root binary backup validated", File.Exists(Path.Combine(adbBackup, "boot.img")) && fake.Calls.Any(a => a.Contains("dd if=/dev/block/by-name/boot bs=1048576 2>/dev/null")));
var self = Environment.ProcessPath!; var runner = new NativeToolRunner(self, self); var binary = Path.Combine(root, "binary.out");
var result = await runner.RunAsync(Transport.AdbRoot, new[] { "--fake-tool" }, binary, TimeSpan.FromSeconds(15), null, ct);
Check("native stream preserves bytes with concurrent stderr", result.Ok && (await File.ReadAllBytesAsync(binary)).SequenceEqual(Enumerable.Range(0, 256).Select(i => (byte)i)));
var argTest = await runner.RunAsync(Transport.Fastboot, new[] { "--fake-tool", "args", "path with spaces & $.img", "boot_a" }, null, TimeSpan.FromSeconds(15), null, ct);
Check("argument list has no shell interpolation", JsonSerializer.Deserialize<string[]>(argTest.Output)!.SequenceEqual(new[] { "path with spaces & $.img", "boot_a" }));
var failResult = await runner.RunAsync(Transport.Fastboot, new[] { "--fake-tool", "fail" }, null, TimeSpan.FromSeconds(15), null, ct); Check("exit code preserved", failResult.ExitCode == 7);
await Reject("native read process timeout", () => runner.RunAsync(Transport.AdbRoot, new[] { "--fake-tool", "sleep" }, null, TimeSpan.FromMilliseconds(150), null, ct));
fake.SupportsFetch = false;
var unsupported = await engine.InspectAsync(target, ct);
Check("fetch capability detected before backup", unsupported.Fetch == FetchSupport.Unsupported);
var unsupportedOutput = Path.Combine(root, "unsupported-fetch");
await Reject("unsupported fetch rejected before creating output", () => engine.BackupAsync(unsupported, unsupported.Partitions, unsupportedOutput, false, false, ct));
Check("unsupported backup leaves no empty backup folder", !Directory.Exists(unsupportedOutput));
fake.SupportsFetch = true;
foreach (var bad in new[] { "CON.img", "name./boot.img", "a//boot.img", "LPT1.img" }) await Reject("reject platform-aliased archive path " + bad, () => Task.FromResult(FlashEngine.SafeChild(root, bad)));
// Refactored command, erase and mode-switch paths must retain the same write guarantees.
fake.Calls.Clear(); await engine.RunCommandAsync(target, "getvar \"current-slot\"", ct);
Check("query remains bound to explicit serial", fake.Calls.Any(a => a.SequenceEqual(new[] { "-s", "TEST-ONLY", "getvar", "current-slot" })));
foreach (var command in new[] { "-s OTHER getvar all", "flashing unlock", "oem unlock", "flash boot_a file.img", "erase boot_a", "-w", "getvar all\nreboot", "getvar \"all", "getvar all extra", "oem edl" })
    await Reject("reject unsafe query: " + command.Replace('\n', ' '), () => engine.RunCommandAsync(target, command, ct));
await Reject("Fastboot command cannot accidentally run through ADB", () => engine.RunCommandAsync(adbDetails.Target, "getvar all", ct));
fake.Unlocked = "no"; await Reject("erase rechecks current lock", () => engine.EraseAsync(details, details.Partitions, true, ct)); fake.Unlocked = "yes";
await Reject("ADB is never an erase transport", () => engine.EraseAsync(adbDetails, adbDetails.Partitions, true, ct));
await Reject("erase rejects duplicate partitions", () => engine.EraseAsync(details, new[] { details.Partitions[0], details.Partitions[0] }, false, ct));
await Reject("erase rejects injected partition name", () => engine.EraseAsync(details, new[] { new Partition("../boot", 4096) }, true, ct));
fake.Erases = 0; fake.FailPartition = "vbmeta_a"; await Reject("all erase targets checked before first erase", () => engine.EraseAsync(details, details.Partitions, false, ct)); Check("erase preflight failure leaves all partitions intact", fake.Erases == 0); fake.FailPartition = "";
fake.Unlocked = ""; await Reject("unknown lock requires explicit erase acknowledgement", () => engine.EraseAsync(details, details.Partitions, false, ct)); fake.Unlocked = "yes";
fake.Erases = 0; fake.Logical = true; await Reject("logical erase requires fastbootd", () => engine.EraseAsync(details, details.Partitions, false, ct)); Check("logical erase zero writes", fake.Erases == 0); fake.Logical = false;
fake.Erases = 0; fake.FailErase = true; await Reject("erase fails on first error", () => engine.EraseAsync(details, details.Partitions, false, ct)); Check("failed erase does not continue", fake.Erases == 1); fake.FailErase = false;
fake.Erases = 0; fake.OnErase = () => engine.RequestStop(); await Reject("stop waits current erase and cancels remaining", () => engine.EraseAsync(details, details.Partitions, false, ct)); Check("erase stops after current partition", fake.Erases == 1); fake.OnErase = null;
fake.Erases = 0; await engine.EraseAsync(details, details.Partitions, false, ct); Check("new erase resets previous stop request", fake.Erases == details.Partitions.Count); Check("erase process has no mid-write cancellation or timeout", fake.EraseUninterruptible);
fake.Calls.Clear(); await engine.RebootAsync(adbDetails.Target, "edl", ct); Check("ADB EDL uses reboot edl", fake.Calls.Any(a => a.SequenceEqual(new[] { "-s", "ROOT-TEST", "reboot", "edl" })));
fake.Calls.Clear(); await engine.RebootAsync(target, "edl", ct); Check("Fastboot EDL uses vendor oem command", fake.Calls.Any(a => a.SequenceEqual(new[] { "-s", "TEST-ONLY", "oem", "edl" })));
var nullEntries=Zip("null-manifest.zip",("manifest.json","{\"Format\":\"DashFile.PartitionBackup/1\",\"Complete\":true,\"Entries\":null,\"Requested\":null}"));
await Reject("null manifest collections rejected",()=>FlashEngine.ExtractBackupAsync(nullEntries,Path.Combine(root,"bad-staging"),ct));
var forged=Zip("forged-length.zip",("manifest.json",JsonSerializer.Serialize(manifest)),("boot_a.img","short"),("vbmeta_a.img","short"));
await Reject("ZIP lengths must match manifest before extraction",()=>FlashEngine.ExtractBackupAsync(forged,Path.Combine(root,"bad-staging"),ct));
Check("rejected archive creates no staging directory",!Directory.Exists(Path.Combine(root,"bad-staging")));
Console.WriteLine($"{passed} passed, {failed} failed. Fixtures: {root}"); return failed == 0 ? 0 : 1;

sealed class FakeRunner : IToolRunner
{
    public bool SupportsFetch = true; public string Product = "test-product"; public bool WriteUninterruptible = true; public string Unlocked = "yes"; public bool Logical; public int Size = 4096; public string FailPartition = "";
    public int Erases; public bool FailErase; public bool EraseUninterruptible = true; public Action? OnErase;
    public bool FailWrite, ShortRead; public int Writes; public Action? OnWrite;
    public List<string[]> Calls { get; } = new();
    public string All => $"(bootloader) product: {Product}\n(bootloader) current-slot: a\n(bootloader) is-userspace: no\n(bootloader) unlocked: {Unlocked}\n(bootloader) partition-size:boot_a: 0x1000\n(bootloader) partition-size:vbmeta_a: 0x1000\n";
    public async Task<ToolResult> RunAsync(Transport t, IReadOnlyList<string> args, string? stdoutFile, TimeSpan? timeout, Action<string>? line, CancellationToken ct)
    {
        var a = args.ToArray(); Calls.Add(a);
        if (t == Transport.AdbRoot)
        {
            if (a.Contains("id -u")) return new(0, "0\n");
            if (a.Contains("ro.product.device")) return new(0, "test-product\n");
            if (a.Contains("ro.boot.slot_suffix")) return new(0, "_a\n");
            if (stdoutFile != null) { await File.WriteAllBytesAsync(stdoutFile, new byte[4096], ct); return new(0, ""); }
            return new(0, "boot|4096|/dev/block/by-name/boot\n");
        }
        if (a.Length > 3 && a[2] == "getvar")
        {
            var key = a[3]; if (key == "max-fetch-size") return new(SupportsFetch ? 0 : 1, SupportsFetch ? "max-fetch-size: 0x1000000" : "FAILED remote: GetVar Variable Not found"); if (key == "all") return new(0, All);
            if (key.StartsWith("has-slot:")) return new(0, key + ": " + (key.EndsWith(":boot") ? "yes" : "no"));
            if (key.StartsWith("is-logical:")) return new(0, key + ": " + (Logical ? "yes" : "no"));
            if (key.StartsWith("partition-size:")) return new(0, key + ": " + (key.EndsWith(FailPartition) && FailPartition != "" ? "0" : Size.ToString("x")));
            return new(0, key + ": " + (key == "unlocked" ? Unlocked : ""));
        }
        if (a.Contains("erase")) { EraseUninterruptible &= !ct.CanBeCanceled && timeout == null; Erases++; OnErase?.Invoke(); return new(FailErase ? 1 : 0, FailErase ? "FAILED fake erase" : "OKAY"); }
        if (a.Contains("fetch")) { await File.WriteAllBytesAsync(a[^1], new byte[ShortRead ? 128 : 4096], ct); return new(0, "OKAY"); }
        if (a.Contains("flash") || a.Contains("update")) { WriteUninterruptible &= !ct.CanBeCanceled && timeout == null; Writes++; OnWrite?.Invoke(); return new(FailWrite ? 1 : 0, FailWrite ? "FAILED fake write" : "OKAY"); }
        return new(0, "");
    }
}
