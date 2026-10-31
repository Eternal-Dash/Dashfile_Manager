using System;

namespace DashFile.Services;

/// <summary>
/// Bootloader 解锁状态判定。
///
/// 只看 getprop 是不准的：隐藏类模块（Shamiko / Zygisk 系）会把
/// <c>ro.boot.verifiedbootstate</c> 伪造成 green、<c>ro.boot.flash.locked</c> 伪造成 1，
/// 于是本来已解锁的机器被读成"未解锁"。这里改成多来源交叉，再用 root 是否真的可用做反证：
/// 原厂锁着 BL 的设备拿不到 root，所以只要 root 可用，就不可能是锁定状态。
/// 另外单独读 /proc/cmdline——隐藏模块主要 hook 属性接口，文件内容往往还是真值。
/// </summary>
internal static class BootloaderState
{
    public enum Verdict
    {
        /// <summary>读不到有效信息</summary>
        Unknown,
        /// <summary>各来源一致显示锁定</summary>
        Locked,
        /// <summary>有来源明确显示已解锁</summary>
        Unlocked,
        /// <summary>属性说锁定、但 root 可用 → 状态被隐藏模块伪装过</summary>
        UnlockedSpoofed,
        /// <summary>属性读不到，仅凭 root 反推已解锁</summary>
        UnlockedByRoot,
    }

    public sealed class Signals
    {
        public string FlashLocked { get; init; } = "";
        public string VerifiedBootState { get; init; } = "";
        public string VbmetaState { get; init; } = "";
        public string SecureBootLock { get; init; } = "";
        /// <summary>/proc/cmdline 原文</summary>
        public string Cmdline { get; init; } = "";
        /// <summary>能否真的拿到 root shell（或已识别出 root 实现）</summary>
        public bool RootAvailable { get; init; }
    }

    public static Verdict Judge(Signals s)
    {
        var locked = Norm(s.FlashLocked);
        var vbs = Norm(s.VerifiedBootState);
        var vbmeta = Norm(s.VbmetaState);
        var sbl = Norm(s.SecureBootLock);
        var cmd = s.Cmdline ?? "";

        // ① 任何一处明说"已解锁"，以它为准
        if (locked == "0" ||
            vbmeta == "unlocked" ||
            sbl == "unlocked" ||
            vbs is "orange" or "red" or "yellow" ||
            CmdHas(cmd, "verifiedbootstate=orange") ||
            CmdHas(cmd, "verifiedbootstate=red") ||
            CmdHas(cmd, "verifiedbootstate=yellow") ||
            CmdHas(cmd, "flash.locked=0"))
        {
            return Verdict.Unlocked;
        }

        var lockedEvidence =
            locked == "1" ||
            vbmeta == "locked" ||
            sbl == "locked" ||
            vbs == "green" ||
            CmdHas(cmd, "verifiedbootstate=green") ||
            CmdHas(cmd, "flash.locked=1");

        // ② 属性说锁定，但 root 真的可用 → 只可能是被伪装
        if (lockedEvidence)
            return s.RootAvailable ? Verdict.UnlockedSpoofed : Verdict.Locked;

        var hasAny = locked.Length > 0 || vbs.Length > 0 || vbmeta.Length > 0 || sbl.Length > 0;

        // ③ 一个属性都没读到：有 root 就是解锁，否则未知
        return s.RootAvailable ? Verdict.UnlockedByRoot : (hasAny ? Verdict.Unknown : Verdict.Unknown);
    }

    public static string Describe(Verdict v) => v switch
    {
        Verdict.Unlocked => "已解锁（BL 解锁）",
        Verdict.UnlockedSpoofed => "已解锁（状态被隐藏模块伪装）",
        Verdict.UnlockedByRoot => "已解锁（由 root 反证）",
        Verdict.Locked => "未解锁（BL 锁定）",
        _ => "未知",
    };

    private static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();

    private static bool CmdHas(string cmd, string token)
        => cmd.Length > 0 && cmd.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
}
