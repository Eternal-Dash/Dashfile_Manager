using System;
using System.IO;
using System.Text.Json;

namespace DashFile.Services;

/// <summary>用户设置（主题色、窗口尺寸、欢迎页是否已勾选不再显示），存到 %LOCALAPPDATA%\DashFile\settings.json</summary>
public sealed class AppSettings
{
    public string AccentHex { get; set; } = "#FFFFFF";
    public bool ThemeDefaultV2 { get; set; }
    public string StyleKey { get; set; } = "m3";
    public double WindowWidth { get; set; } = 1135;
    public double WindowHeight { get; set; } = 707;

    /// <summary>欢迎页勾过「以后不再显示」后置真，以后再启动就不再弹</summary>
    public bool WelcomeHidden { get; set; }

    private static AppSettings? _current;
    private static string Dir => AppPaths.LocalRoot;
    private static string FilePath => AppPaths.Settings;

    public static AppSettings Current
    {
        get
        {
            if (_current != null) return _current;
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) { _current = s; return _current; }
                }
            }
            catch { }
            _current = new AppSettings();
            return _current;
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(Current,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Add("保存设置失败：" + ex.Message); }
    }
}
