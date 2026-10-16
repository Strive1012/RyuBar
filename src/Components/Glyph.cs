using System.Windows;
using System.Windows.Media;

namespace RyuBar.Components;

/// <summary>
/// 传输控件与占位图形的几何。用几何而不是字体图标：
/// 不依赖系统里装了什么字体，缩放也不会糊。
/// 每个方法都按给定方框居中，返回冻结的几何（可跨帧复用）。
/// </summary>
internal static class Glyph
{
    public static Geometry Play(Rect box)
    {
        double s = Math.Min(box.Width, box.Height) * 0.42;
        double cx = box.X + box.Width / 2;
        double cy = box.Y + box.Height / 2;

        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(cx - s * 0.40, cy - s * 0.64), true, true);
            context.LineTo(new Point(cx + s * 0.66, cy), true, false);
            context.LineTo(new Point(cx - s * 0.40, cy + s * 0.64), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    public static Geometry Pause(Rect box)
    {
        double s = Math.Min(box.Width, box.Height) * 0.42;
        double cx = box.X + box.Width / 2;
        double cy = box.Y + box.Height / 2;
        double barWidth = s * 0.32;
        double height = s * 1.26;
        double gap = s * 0.40;
        double radius = barWidth * 0.35;

        GeometryGroup group = new();
        group.Children.Add(new RectangleGeometry(new Rect(cx - gap / 2 - barWidth, cy - height / 2, barWidth, height), radius, radius));
        group.Children.Add(new RectangleGeometry(new Rect(cx + gap / 2, cy - height / 2, barWidth, height), radius, radius));
        group.Freeze();
        return group;
    }

    public static Geometry Next(Rect box)
    {
        double s = Math.Min(box.Width, box.Height) * 0.42;
        double cx = box.X + box.Width / 2;
        double cy = box.Y + box.Height / 2;
        double barWidth = s * 0.20;
        double height = s * 1.26;

        GeometryGroup group = new();
        StreamGeometry triangle = new();
        using (StreamGeometryContext context = triangle.Open())
        {
            context.BeginFigure(new Point(cx - s * 0.62, cy - s * 0.58), true, true);
            context.LineTo(new Point(cx + barWidth * 0.4, cy), true, false);
            context.LineTo(new Point(cx - s * 0.62, cy + s * 0.58), true, false);
        }
        triangle.Freeze();
        group.Children.Add(triangle);
        group.Children.Add(new RectangleGeometry(new Rect(cx + barWidth * 0.9, cy - height / 2, barWidth, height), barWidth * 0.35, barWidth * 0.35));
        group.Freeze();
        return group;
    }

    public static Geometry Previous(Rect box)
    {
        double s = Math.Min(box.Width, box.Height) * 0.42;
        double cx = box.X + box.Width / 2;
        double cy = box.Y + box.Height / 2;
        double barWidth = s * 0.20;
        double height = s * 1.26;

        GeometryGroup group = new();
        group.Children.Add(new RectangleGeometry(new Rect(cx - barWidth * 1.9, cy - height / 2, barWidth, height), barWidth * 0.35, barWidth * 0.35));
        StreamGeometry triangle = new();
        using (StreamGeometryContext context = triangle.Open())
        {
            context.BeginFigure(new Point(cx + s * 0.62, cy - s * 0.58), true, true);
            context.LineTo(new Point(cx - barWidth * 0.4, cy), true, false);
            context.LineTo(new Point(cx + s * 0.62, cy + s * 0.58), true, false);
        }
        triangle.Freeze();
        group.Children.Add(triangle);
        group.Freeze();
        return group;
    }

    /// <summary>没有封面时的音符占位。</summary>
    public static Geometry Note(Rect box)
    {
        double s = Math.Min(box.Width, box.Height) * 0.52;
        double cx = box.X + box.Width / 2;
        double cy = box.Y + box.Height / 2;
        double headRadius = s * 0.20;
        double stemWidth = s * 0.13;

        GeometryGroup group = new();
        group.Children.Add(new EllipseGeometry(new Point(cx - s * 0.16, cy + s * 0.30), headRadius, headRadius * 0.86));
        group.Children.Add(new RectangleGeometry(new Rect(cx - s * 0.16 + headRadius - stemWidth / 2, cy - s * 0.52, stemWidth, s * 0.86), stemWidth * 0.3, stemWidth * 0.3));
        StreamGeometry flag = new();
        using (StreamGeometryContext context = flag.Open())
        {
            context.BeginFigure(new Point(cx - s * 0.16 + headRadius - stemWidth / 2, cy - s * 0.52), true, true);
            context.LineTo(new Point(cx + s * 0.34, cy - s * 0.30), true, false);
            context.LineTo(new Point(cx + s * 0.34, cy - s * 0.06), true, false);
            context.LineTo(new Point(cx - s * 0.16 + headRadius + stemWidth / 2, cy - s * 0.26), true, false);
        }
        flag.Freeze();
        group.Children.Add(flag);
        group.Freeze();
        return group;
    }
}
