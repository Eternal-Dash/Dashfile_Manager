using System;
using Avalonia;
using Avalonia.Controls;

namespace DashFile.Views;

/// <summary>把窗口钳制到当前屏幕的工作区，避免居中到超宽窗体后跑到屏幕外。</summary>
internal static class WindowPlacement
{
    public static void ClampToWorkingArea(Window w)
    {
        try
        {
            var screen = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary;
            if (screen == null) return;
            var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            var wa = screen.WorkingArea;
            var wPx = (int)Math.Round(w.Bounds.Width * scale);
            var hPx = (int)Math.Round(w.Bounds.Height * scale);
            var x = w.Position.X;
            var y = w.Position.Y;
            var maxX = wa.X + wa.Width - wPx;
            var maxY = wa.Y + wa.Height - hPx;
            if (x > maxX) x = maxX;
            if (y > maxY) y = maxY;
            if (x < wa.X) x = wa.X;
            if (y < wa.Y) y = wa.Y;
            w.Position = new PixelPoint(x, y);
        }
        catch { }
    }
}
