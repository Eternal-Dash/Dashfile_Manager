namespace DashFile.PluginApi;

/// <summary>
/// 设备端 shell 的常用工具。插件拼命令时务必用 <see cref="Quote"/> 包住路径和用户输入，
/// 否则空格、引号、$(...) 会被 shell 解释。
/// </summary>
public static class PluginShell
{
    /// <summary>包成 POSIX 单引号字面量。</summary>
    public static string Quote(string value) => "'" + (value ?? "").Replace("'", "'\\''") + "'";

    /// <summary>远端路径拼接（POSIX 风格）。</summary>
    public static string Join(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory)) directory = "/";
        if (directory == "/") return "/" + name;
        return directory.TrimEnd('/') + "/" + name;
    }

    public static string Parent(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    public static string BaseName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }
}
