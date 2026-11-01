namespace DashFile.Services.Plugins;

/// <summary>Built-in page IDs remain stable while extension tabs precede Settings.</summary>
public static class PluginNavigation
{
    public static int ToVisualIndex(int page, int count) => page == 5 ? 5 + count : page >= 6 ? page - 1 : page;
    public static int ToPageIndex(int visual, int count) => visual == 5 + count ? 5 : visual >= 5 ? visual + 1 : visual;
}
