namespace DashFile.Services;

/// <summary>shell 单引号转义工具</summary>
public static class Sh
{
    /// <summary>把任意字符串包成 shell 单引号字面量</summary>
    public static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>远端路径拼接（POSIX 风格）</summary>
    public static string Join(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) dir = "/";
        if (dir == "/") return "/" + name;
        return dir.TrimEnd('/') + "/" + name;
    }

    public static string Parent(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        if (i <= 0) return "/";
        return p[..i];
    }

    public static string BaseName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i < 0 ? p : p[(i + 1)..];
    }
}
