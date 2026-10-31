using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace DashFile.Services;

/// <summary>SVG pathData（VectorDrawable 用的就是这套语法）→ SKPath</summary>
public static class SvgPathParser
{
    public static SKPath? Parse(string data)
    {
        SKPath? path = null;
        try
        {
            path = new SKPath();
            var t = new Tok(data);
            var guard = 0;
            float cx = 0, cy = 0;          // 当前点
            float sx = 0, sy = 0;          // 子路径起点
            char cmd = 'M';
            float lastCtrlX = 0, lastCtrlY = 0;

            while (true)
            {
                // 兜底：畸形 pathData（如 "M0 0z5"）里 Z 分支不消费任何字符，
                //   以前会在 while(true) 里空转把 UI 线程 100% 打死，且 CancellationToken 无效。
                if (++guard > 200_000) break;

                t.SkipSep();
                if (t.Eof) break;

                var posBefore = t.Pos;
                var ch = t.Peek();
                if (char.IsLetter(ch)) { cmd = ch; t.Next(); }
                else if (cmd == 'M') cmd = 'L';     // 隐式重复：M 之后按 L
                else if (cmd == 'm') cmd = 'l';

                switch (cmd)
                {
                    case 'M': cx = t.Num(); cy = t.Num(); path.MoveTo(cx, cy); sx = cx; sy = cy; break;
                    case 'm': cx += t.Num(); cy += t.Num(); path.MoveTo(cx, cy); sx = cx; sy = cy; break;
                    case 'L': cx = t.Num(); cy = t.Num(); path.LineTo(cx, cy); break;
                    case 'l': cx += t.Num(); cy += t.Num(); path.LineTo(cx, cy); break;
                    case 'H': cx = t.Num(); path.LineTo(cx, cy); break;
                    case 'h': cx += t.Num(); path.LineTo(cx, cy); break;
                    case 'V': cy = t.Num(); path.LineTo(cx, cy); break;
                    case 'v': cy += t.Num(); path.LineTo(cx, cy); break;
                    case 'C':
                        {
                            var x1 = t.Num(); var y1 = t.Num(); var x2 = t.Num(); var y2 = t.Num(); var x = t.Num(); var y = t.Num();
                            path.CubicTo(x1, y1, x2, y2, x, y); lastCtrlX = x2; lastCtrlY = y2; cx = x; cy = y;
                            break;
                        }
                    case 'c':
                        {
                            var x1 = cx + t.Num(); var y1 = cy + t.Num(); var x2 = cx + t.Num(); var y2 = cy + t.Num();
                            var x = cx + t.Num(); var y = cy + t.Num();
                            path.CubicTo(x1, y1, x2, y2, x, y); lastCtrlX = x2; lastCtrlY = y2; cx = x; cy = y;
                            break;
                        }
                    case 'S':
                        {
                            var x1 = 2 * cx - lastCtrlX; var y1 = 2 * cy - lastCtrlY;
                            var x2 = t.Num(); var y2 = t.Num(); var x = t.Num(); var y = t.Num();
                            path.CubicTo(x1, y1, x2, y2, x, y); lastCtrlX = x2; lastCtrlY = y2; cx = x; cy = y;
                            break;
                        }
                    case 's':
                        {
                            var x1 = 2 * cx - lastCtrlX; var y1 = 2 * cy - lastCtrlY;
                            var x2 = cx + t.Num(); var y2 = cy + t.Num(); var x = cx + t.Num(); var y = cy + t.Num();
                            path.CubicTo(x1, y1, x2, y2, x, y); lastCtrlX = x2; lastCtrlY = y2; cx = x; cy = y;
                            break;
                        }
                    case 'Q':
                        {
                            var x1 = t.Num(); var y1 = t.Num(); var x = t.Num(); var y = t.Num();
                            path.QuadTo(x1, y1, x, y); cx = x; cy = y;
                            break;
                        }
                    case 'q':
                        {
                            var x1 = cx + t.Num(); var y1 = cy + t.Num(); var x = cx + t.Num(); var y = cy + t.Num();
                            path.QuadTo(x1, y1, x, y); cx = x; cy = y;
                            break;
                        }
                    case 'T':
                        {
                            var x1 = 2 * cx - lastCtrlX; var y1 = 2 * cy - lastCtrlY;
                            var x = t.Num(); var y = t.Num();
                            path.QuadTo(x1, y1, x, y); lastCtrlX = x1; lastCtrlY = y1; cx = x; cy = y; break;
                        }
                    case 't':
                        {
                            var x1 = 2 * cx - lastCtrlX; var y1 = 2 * cy - lastCtrlY;
                            var x = cx + t.Num(); var y = cy + t.Num();
                            path.QuadTo(x1, y1, x, y); lastCtrlX = x1; lastCtrlY = y1; cx = x; cy = y; break;
                        }
                    case 'A': case 'a':
                        {
                            var rx = t.Num(); var ry = t.Num(); var rot = t.Num();
                            var large = t.Flag(); var sweep = t.Flag();
                            var ex = t.Num(); var ey = t.Num();
                            if (cmd == 'a') { ex += cx; ey += cy; }
                            AddArc(path, cx, cy, rx, ry, rot, large, sweep, ex, ey);
                            cx = ex; cy = ey;
                            break;
                        }
                    case 'Z': case 'z':
                        path.Close();
                        cx = sx; cy = sy;
                        break;
                    default:
                        t.Next();       // 未知命令，跳过
                        break;
                }

                if (t.Eof) break;
                if (t.Pos == posBefore) t.Next();   // 本趟没前进一个字符：强制吃掉一个，绝不允许死循环
            }

            return path;
        }
        catch
        {
            path?.Dispose();
            return null;
        }
    }

    /// <summary>SVG 圆弧 → 中心参数化 → 三次贝塞尔</summary>
    private static void AddArc(SKPath path, float x0, float y0, float rx, float ry,
        float rotationDeg, bool largeArc, bool sweep, float x, float y)
    {
        if (rx == 0 || ry == 0) { path.LineTo(x, y); return; }
        rx = Math.Abs(rx); ry = Math.Abs(ry);

        var phi = rotationDeg * Math.PI / 180.0;
        var cosPhi = Math.Cos(phi); var sinPhi = Math.Sin(phi);

        var dx = (x0 - x) / 2.0; var dy = (y0 - y) / 2.0;
        var x1p = cosPhi * dx + sinPhi * dy;
        var y1p = -sinPhi * dx + cosPhi * dy;

        var lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
        if (lambda > 1) { var s = Math.Sqrt(lambda); rx = (float)(rx * s); ry = (float)(ry * s); }

        var sign = largeArc != sweep ? 1.0 : -1.0;
        var num = (double)rx * rx * ry * ry - (double)rx * rx * y1p * y1p - (double)ry * ry * x1p * x1p;
        var den = (double)rx * rx * y1p * y1p + (double)ry * ry * x1p * x1p;
        var coef = den == 0 ? 0 : sign * Math.Sqrt(Math.Max(0, num / den));

        var cxp = coef * rx * y1p / ry;
        var cyp = -coef * ry * x1p / rx;

        var cx = cosPhi * cxp - sinPhi * cyp + (x0 + x) / 2.0;
        var cy = sinPhi * cxp + cosPhi * cyp + (y0 + y) / 2.0;

        double Angle(double ux, double uy, double vx, double vy)
        {
            var dot = ux * vx + uy * vy;
            var len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            if (len == 0) return 0;
            var a = Math.Acos(Math.Clamp(dot / len, -1, 1));
            return ux * vy - uy * vx < 0 ? -a : a;
        }

        var theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        var dTheta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && dTheta > 0) dTheta -= 2 * Math.PI;
        if (sweep && dTheta < 0) dTheta += 2 * Math.PI;

        var segments = (int)Math.Ceiling(Math.Abs(dTheta) / (Math.PI / 2));
        if (segments <= 0) segments = 1;
        var delta = dTheta / segments;
        var k = 4.0 / 3.0 * Math.Tan(delta / 4);

        var t1 = theta1;
        double Ex(double th) => cx + rx * Math.Cos(th) * cosPhi - ry * Math.Sin(th) * sinPhi;
        double Ey(double th) => cy + rx * Math.Cos(th) * sinPhi + ry * Math.Sin(th) * cosPhi;
        double Dx(double th) => -rx * Math.Sin(th) * cosPhi - ry * Math.Cos(th) * sinPhi;
        double Dy(double th) => -rx * Math.Sin(th) * sinPhi + ry * Math.Cos(th) * cosPhi;

        for (var i = 0; i < segments; i++)
        {
            var t2 = t1 + delta;
            var x1 = Ex(t1) + k * Dx(t1);
            var y1 = Ey(t1) + k * Dy(t1);
            var x2 = Ex(t2) - k * Dx(t2);
            var y2 = Ey(t2) - k * Dy(t2);
            var x3 = Ex(t2);
            var y3 = Ey(t2);
            path.CubicTo((float)x1, (float)y1, (float)x2, (float)y2, (float)x3, (float)y3);
            t1 = t2;
        }
    }

    private sealed class Tok
    {
        private readonly string _s;
        private int _i;
        public Tok(string s) => _s = s;
        public bool Eof => _i >= _s.Length;
        public int Pos => _i;

        public char Peek() => _s[_i];
        public void Next() => _i++;
        public void SkipSep()
        {
            while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
        }

        public bool Flag()
        {
            SkipSep();
            if (_i >= _s.Length) return false;
            var c = _s[_i];
            if (c == '0' || c == '1') { _i++; return c == '1'; }
            return Num() != 0;
        }

        public float Num()
        {
            SkipSep();
            var start = _i;
            if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                _i++;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            if (start == _i) return 0;
            return float.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
