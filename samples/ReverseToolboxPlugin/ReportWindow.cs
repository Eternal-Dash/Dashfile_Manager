using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ReverseToolboxPlugin;

/// <summary>可滚动、可另存的报告窗口（长文本不走对话框）。</summary>
internal sealed class ReportWindow : Window
{
    public ReportWindow(string title, string text, Func<string, Task>? save)
    {
        Title = title;
        Width = 940;
        Height = 700;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        try
        {
            if (Application.Current?.TryGetResource("Bg", null, out var bg) == true && bg is IBrush b)
                Background = b;
        }
        catch { }

        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var saveBtn = new Button { Content = "保存为…" };
        saveBtn.Classes.Add("tool");
        saveBtn.Click += async (_, _) =>
        {
            if (save != null) await save(title);
        };
        var closeBtn = new Button { Content = "关闭" };
        closeBtn.Classes.Add("tool");
        closeBtn.Click += (_, _) => Close();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { saveBtn, closeBtn },
        };

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(box);
        Content = root;
    }
}
