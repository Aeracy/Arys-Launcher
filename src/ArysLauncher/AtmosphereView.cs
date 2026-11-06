using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace ArysLauncher;

public sealed class AtmosphereView : FrameworkElement
{
    private static readonly Brush StarBrush = Freeze(new SolidColorBrush(Color.FromRgb(246, 205, 234)));
    private static readonly Pen RingPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(43, 237, 196, 225)), 0.7));
    private static readonly Pen InnerPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(38, 228, 201, 166)), 0.6));
    private static readonly Pen GlowPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(22, 231, 172, 224)), 5));
    private static readonly Pen FinePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(66, 224, 185, 228)), 0.65));
    private static readonly Brush GoldBrush = Freeze(new SolidColorBrush(Color.FromRgb(244, 215, 172)));
    private static readonly Brush HaloBrush = CreateHalo();
    private static readonly Point[] Circle = Enumerable.Range(0, 97).Select(index =>
        new Point(Math.Cos(index * Math.PI / 48), Math.Sin(index * Math.PI / 48))).ToArray();
    private readonly (double X, double Y, double Phase)[] stars;
    private long previousTime;
    private double seconds;
    private Size orbitSize;
    private StreamGeometry? outerOrbit;
    private StreamGeometry? innerOrbit;
    private StreamGeometry? glowOrbit;
    private bool reducedMotion;
    private bool subscribed;

    public bool ReducedMotion
    {
        get
        {
            return reducedMotion;
        }
        set
        {
            reducedMotion = value;
            UpdateSubscription();
            InvalidateVisual();
        }
    }

    public AtmosphereView()
    {
        stars = Enumerable.Range(0, 32).Select(index =>
            ((index * 0.61803398875) % 1, (index * 0.38196601125) % 1, index * 2.399963)).ToArray();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    private void UpdateSubscription()
    {
        if (IsLoaded && IsVisible && !reducedMotion && !subscribed)
        {
            previousTime = 0;
            CompositionTarget.Rendering += Frame;
            subscribed = true;
        }
        else if ((!IsLoaded || !IsVisible || reducedMotion) && subscribed)
        {
            Unsubscribe();
        }
    }

    private void Unsubscribe()
    {
        CompositionTarget.Rendering -= Frame;
        subscribed = false;
        previousTime = 0;
    }

    private void Frame(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (Window.GetWindow(this)?.WindowState == WindowState.Minimized)
        {
            previousTime = 0;
            return;
        }
        if (previousTime != 0)
        {
            seconds += Math.Clamp(Stopwatch.GetElapsedTime(previousTime, now).TotalSeconds, 0, 0.1);
        }
        previousTime = now;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 80 || ActualHeight < 80)
        {
            return;
        }
        var center = new Point(ActualWidth * 0.53, ActualHeight * 0.51);
        var radius = Math.Min(ActualWidth * 0.36, ActualHeight * 0.37);
        if (orbitSize != RenderSize)
        {
            orbitSize = RenderSize;
            outerOrbit = CreateOrbit(center, radius + 15, 8);
            innerOrbit = CreateOrbit(center, radius - 23, 12);
            glowOrbit = CreateOrbit(center, radius + 19, 8);
        }
        var breath = 0.68 + Math.Sin(seconds * 0.42) * 0.15;
        drawing.PushOpacity(breath);
        drawing.DrawEllipse(HaloBrush, null, center, radius * 1.5, radius * 1.5);
        drawing.Pop();
        drawing.DrawEllipse(null, RingPen, center, radius, radius);
        drawing.DrawEllipse(null, InnerPen, center, radius - 8, radius - 8);
        drawing.PushTransform(new RotateTransform(seconds * 2, center.X, center.Y));
        drawing.DrawGeometry(null, FinePen, outerOrbit);
        drawing.DrawGeometry(null, GlowPen, glowOrbit);
        drawing.Pop();
        drawing.PushTransform(new RotateTransform(-seconds * 3, center.X, center.Y));
        drawing.DrawGeometry(null, InnerPen, innerOrbit);
        drawing.Pop();
        for (var index = 0; index < 12; index++)
        {
            var angle = index * Math.PI / 6 + seconds * 0.045;
            drawing.DrawLine(InnerPen,
                new Point(center.X + Math.Cos(angle) * (radius - 4), center.Y + Math.Sin(angle) * (radius - 4)),
                new Point(center.X + Math.Cos(angle) * (radius + 4), center.Y + Math.Sin(angle) * (radius + 4)));
        }
        for (var index = 0; index < 4; index++)
        {
            var angle = seconds * (0.08 + index * 0.01) + index * Math.PI / 2;
            var orbit = radius + 14;
            var point = new Point(center.X + Math.Cos(angle) * orbit, center.Y + Math.Sin(angle) * orbit);
            drawing.PushOpacity(0.55);
            drawing.DrawEllipse(index % 2 == 0 ? GoldBrush : StarBrush, null, point, 1.8, 1.8);
            drawing.DrawLine(FinePen, new Point(point.X - 4, point.Y), new Point(point.X + 4, point.Y));
            drawing.DrawLine(FinePen, new Point(point.X, point.Y - 4), new Point(point.X, point.Y + 4));
            drawing.Pop();
        }
        for (var index = 0; index < stars.Length; index++)
        {
            var star = stars[index];
            var x = star.X * ActualWidth + Math.Sin(seconds * 0.15 + star.Phase) * 5;
            var y = ((star.Y * ActualHeight - seconds * (2 + index % 4)) % ActualHeight + ActualHeight) % ActualHeight;
            var opacity = 0.12 + (Math.Sin(seconds * 0.7 + star.Phase) + 1) * 0.17;
            var size = index % 8 == 0 ? 1.4 : 0.65;
            drawing.PushOpacity(opacity);
            drawing.DrawEllipse(index % 7 == 0 ? GoldBrush : StarBrush, null, new Point(x, y), size, size);
            if (index % 11 == 0)
            {
                drawing.DrawLine(InnerPen, new Point(x, y + 3), new Point(x + Math.Sin(star.Phase) * 2, y + 17));
            }
            drawing.Pop();
        }
    }

    private static StreamGeometry CreateOrbit(Point center, double radius, int period)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (var index = 0; index < Circle.Length - 1; index++)
            {
                if (index % period < 3)
                {
                    continue;
                }
                var from = Circle[index];
                var to = Circle[index + 1];
                path.BeginFigure(new Point(center.X + from.X * radius, center.Y + from.Y * radius), false, false);
                path.LineTo(new Point(center.X + to.X * radius, center.Y + to.Y * radius), true, false);
            }
        }
        return Freeze(geometry);
    }

    private static Brush CreateHalo()
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(32, 220, 139, 225), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(14, 192, 133, 219), 0.55));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        return Freeze(brush);
    }

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
