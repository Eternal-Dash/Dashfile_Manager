using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DashFile.Controls;

/// <summary>0~100 的实时折线图（CPU 占用）</summary>
public class SparkLine : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<SparkLine, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<SparkLine, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SparkLine, IBrush?>(nameof(FillBrush));

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    static SparkLine()
    {
        AffectsRender<SparkLine>(ValuesProperty, LineBrushProperty, FillBrushProperty);
    }

    public override void Render(DrawingContext context)
    {
        var v = Values;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 2 || h <= 2) return;

        // 网格（没有数据时也画，避免看起来像一块空白）
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1);
        for (int i = 1; i < 4; i++)
        {
            var y = h * i / 4.0;
            context.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        if (v == null || v.Count == 0) return;

        // 样本不足时先画一个点，别让图空着
        if (v.Count == 1)
        {
            var y0 = h - (Math.Clamp(v[0], 0, 100) / 100.0) * (h - 2) - 1;
            context.DrawEllipse(LineBrush ?? Brushes.White, null, new Point(w - 1.5, y0), 2.2, 2.2);
            return;
        }

        double step = w / (v.Count - 1);
        var pts = new List<Point>(v.Count);
        for (int i = 0; i < v.Count; i++)
        {
            var val = Math.Clamp(v[i], 0, 100);
            pts.Add(new Point(i * step, h - (val / 100.0) * (h - 2) - 1));
        }

        // 填充
        if (FillBrush != null)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, h), true);
                foreach (var p in pts) g.LineTo(p);
                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }
            context.DrawGeometry(FillBrush, null, geo);
        }

        // 折线
        var pen = new Pen(LineBrush ?? Brushes.White, 1.6);
        for (int i = 1; i < pts.Count; i++)
            context.DrawLine(pen, pts[i - 1], pts[i]);
    }
}
