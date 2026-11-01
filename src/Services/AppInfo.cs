namespace DashFile.Services;

/// <summary>
/// 应用级常量。版本号、署名、产品名只在这里改一次，
/// 避免以前 VM / XAML / 发布脚本 / 文档各写一份。
/// </summary>
public static class AppInfo
{
    public const string Name = "DashFile";
    public const string Product = "DashFile — 安卓文件管理器";
    public const string Version = "1.2.0";
    public const string Signature = "酷安@恒定破折号";
    public const string Homepage = "";

    /// <summary>adb 调试用的 shell 包名（超级用户页要固定显示它）。</summary>
    public const string ShellPackage = "com.android.shell";
}
