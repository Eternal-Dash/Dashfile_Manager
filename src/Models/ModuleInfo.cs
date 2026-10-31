namespace DashFile.Models;

public sealed class ModuleInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string Dir { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool HasWebUI { get; init; }
    public bool HasAction { get; init; }
    public bool PendingRemove { get; init; }

    public string ToggleText => PendingRemove ? "即将卸载" : (Enabled ? "已启用" : "已停用");
    public string SubLine => string.IsNullOrWhiteSpace(Author) ? Version : $"{Author}  ·  {Version}";
}
