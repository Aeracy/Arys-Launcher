using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArysLauncher;

internal sealed class StarfieldBackdrop : ContainerVisual
{
    private static readonly BitmapSource[] Sprites =
    {
        CreateSprite(Color.FromRgb(195, 166, 228)),
        CreateSprite(Color.FromRgb(231, 189, 216)),
        CreateSprite(Color.FromRgb(235, 208, 163))
    };
    private readonly Random random = new(Random.Shared.Next());
    private readonly DrawingVisual horizon = new();
    private readonly DrawingVisual mist = new();
    private readonly TranslateTransform mistPosition = new();
    private readonly Star[] stars = new Star[96];
    private readonly Meteor[] meteors = { new(), new() };
    private Size size;
    private double nextMeteor;

    public StarfieldBackdrop()
    {
        Children.Add(horizon);
        mist.Transform = mistPosition;
        Children.Add(mist);
        for (var index = 0; index < stars.Length; index++)
        {
            var star = new Star(index % 3);
            stars[index] = star;
            ResetStar(star, initial: true);
            Children.Add(star.Visual);
        }
        foreach (var meteor in meteors)
        {
            Children.Add(meteor.Visual);
        }
        nextMeteor = Between(0.8, 1.8);
    }

    public void Resize(Size value)
    {
        if (size == value || value.Width < 80 || value.Height < 80)
        {
            return;
        }
        size = value;
        Clip = Freeze(new RectangleGeometry(new Rect(size)));
        using (var drawing = horizon.RenderOpen())
        {
            var scene = new DrawingGroup();
            using (var background = scene.Open())
            {
                background.DrawEllipse(Mist(Color.FromArgb(68, 111, 67, 152)), null,
                    new Point(size.Width * 0.82, size.Height * 0.13), size.Width * 0.66, size.Height * 0.73);
                background.DrawEllipse(Mist(Color.FromArgb(36, 168, 93, 160)), null,
                    new Point(size.Width * 0.14, size.Height * 0.88), size.Width * 0.56, size.Height * 0.62);
                background.DrawEllipse(Mist(Color.FromArgb(146, 7, 5, 18)), null,
                    new Point(size.Width * 0.43, size.Height * 0.57), size.Width * 0.55, size.Height * 0.67);
            }
            var brush = new DrawingBrush(Freeze(scene)) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size) };
            RenderOptions.SetCachingHint(brush, CachingHint.Cache);
            drawing.DrawRectangle(Freeze(brush), null, new Rect(size));
            var orbit = new Pen(new SolidColorBrush(Color.FromArgb(13, 209, 174, 228)), 0.7);
            var center = new Point(size.Width * 0.85, size.Height * 0.18);
            var radius = Math.Min(size.Width * 0.38, size.Height * 0.62);
            drawing.DrawEllipse(null, Freeze(orbit), center, radius, radius);
            drawing.DrawEllipse(null, orbit, center, radius - 14, radius - 14);
        }
        using (var drawing = mist.RenderOpen())
        {
            var scene = new DrawingGroup();
            using (var cloud = scene.Open())
            {
                cloud.DrawEllipse(Mist(Color.FromArgb(32, 178, 105, 174)), null,
                    new Point(size.Width * 0.77, size.Height * 0.50), size.Width * 0.47, size.Height * 0.70);
            }
            var brush = new DrawingBrush(Freeze(scene)) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size) };
            RenderOptions.SetCachingHint(brush, CachingHint.Cache);
            drawing.DrawRectangle(Freeze(brush), null, new Rect(size));
        }
    }

    public void Update(double seconds, double elapsed, bool reducedMotion)
    {
        if (size.Width < 1 || size.Height < 1)
        {
            return;
        }
        mistPosition.X = Math.Sin(seconds * 0.06) * 24;
        mistPosition.Y = Math.Cos(seconds * 0.045) * 11;
        mist.Opacity = 0.60 + Math.Sin(seconds * 0.17) * 0.18;
        foreach (var star in stars)
        {
            star.Age += elapsed;
            if (star.Age >= star.Lifetime)
            {
                var remainder = star.Age - star.Lifetime;
                ResetStar(star, initial: false);
                star.Age = remainder;
            }
            var x = Wrap(star.Origin.X * size.Width + star.Velocity.X * star.Age, size.Width);
            var y = Wrap(star.Origin.Y * size.Height + star.Velocity.Y * star.Age, size.Height);
            var envelope = Math.Clamp(Math.Min(star.Age, star.Lifetime - star.Age) / 2.5, 0, 1);
            var edge = Math.Clamp(Math.Min(Math.Min(x, size.Width - x), Math.Min(y, size.Height - y)) / 18, 0, 1);
            var shimmer = 0.72 + Math.Sin(seconds * star.Pulse + star.Phase) * 0.28;
            star.Position.X = x;
            star.Position.Y = y;
            star.Visual.Opacity = star.Brightness * envelope * edge * shimmer;
        }
        if (!reducedMotion && seconds >= nextMeteor)
        {
            var available = meteors.FirstOrDefault(meteor => !meteor.Active);
            if (available != null)
            {
                ResetMeteor(available, seconds);
            }
            nextMeteor = seconds + (random.NextDouble() < 0.16 ? Between(0.45, 0.8) : Between(4, 7));
        }
        foreach (var meteor in meteors)
        {
            if (reducedMotion || !meteor.Active)
            {
                meteor.Visual.Opacity = 0;
                continue;
            }
            var age = seconds - meteor.StartedAt;
            var progress = age / meteor.Duration;
            if (progress >= 1)
            {
                meteor.Active = false;
                meteor.Visual.Opacity = 0;
                continue;
            }
            meteor.Position.X = meteor.Origin.X * size.Width + meteor.Velocity.X * age;
            meteor.Position.Y = meteor.Origin.Y * size.Height + meteor.Velocity.Y * age;
            meteor.Visual.Opacity = Math.Sin(progress * Math.PI) * 0.72;
        }
    }

    private void ResetStar(Star star, bool initial)
    {
        var depth = star.Layer + 1;
        star.Lifetime = Between(30, 65) / Math.Sqrt(depth);
        star.Age = initial ? Between(0, star.Lifetime) : 0;
        star.Origin = new Point(random.NextDouble(), random.NextDouble());
        star.Velocity = new Vector(Between(0.8, 2.2) * depth, -Between(1.5, 3.8) * depth);
        star.Phase = Between(0, Math.PI * 2);
        star.Pulse = Between(0.35, 0.85);
        star.Brightness = Between(0.20, 0.37) + star.Layer * 0.11;
        var diameter = Between(3.0, 4.5) + star.Layer * 1.8;
        var tint = random.NextDouble() < 0.12 ? 2 : random.Next(2);
        using var drawing = star.Visual.RenderOpen();
        drawing.DrawImage(Sprites[tint], new Rect(-diameter / 2, -diameter / 2, diameter, diameter));
    }

    private void ResetMeteor(Meteor meteor, double seconds)
    {
        meteor.Active = true;
        meteor.StartedAt = seconds;
        meteor.Duration = Between(0.85, 1.35);
        meteor.Origin = new Point(Between(0.22, 0.85), Between(0.05, 0.48));
        var angle = Between(18, 32);
        var speed = Between(270, 470);
        meteor.Velocity = new Vector(Math.Cos(angle * Math.PI / 180) * speed, Math.Sin(angle * Math.PI / 180) * speed);
        meteor.Rotation.Angle = angle;
        var length = Between(72, 145);
        var trail = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        trail.GradientStops.Add(new GradientStop(Color.FromArgb(0, 186, 149, 215), 0));
        trail.GradientStops.Add(new GradientStop(Color.FromArgb(105, 203, 161, 223), 0.58));
        trail.GradientStops.Add(new GradientStop(Color.FromArgb(236, 246, 223, 241), 1));
        var tail = new StreamGeometry();
        using (var path = tail.Open())
        {
            path.BeginFigure(new Point(-length, 0), true, true);
            path.LineTo(new Point(0, -1.15), true, false);
            path.LineTo(new Point(0, 1.15), true, false);
        }
        using var drawing = meteor.Visual.RenderOpen();
        drawing.DrawGeometry(Freeze(trail), null, Freeze(tail));
        drawing.DrawImage(Sprites[1], new Rect(-7, -7, 14, 14));
    }

    private double Between(double minimum, double maximum)
    {
        return minimum + random.NextDouble() * (maximum - minimum);
    }

    private static double Wrap(double value, double extent)
    {
        return (value % extent + extent) % extent;
    }

    private static Brush Mist(Color color)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(color, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(color.A * 0.42), color.R, color.G, color.B), 0.48));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        return Freeze(brush);
    }

    private static BitmapSource CreateSprite(Color color)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawEllipse(Mist(Color.FromArgb(105, color.R, color.G, color.B)), null, new Point(16, 16), 15, 15);
            drawing.DrawEllipse(Freeze(new SolidColorBrush(Color.FromArgb(160, color.R, color.G, color.B))),
                null, new Point(16, 16), 3.2, 3.2);
            drawing.DrawEllipse(Freeze(new SolidColorBrush(Color.FromRgb(246, 224, 241))),
                null, new Point(16, 16), 1.4, 1.4);
        }
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Freeze(bitmap);
    }

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    private sealed class Star
    {
        public DrawingVisual Visual { get; } = new();
        public TranslateTransform Position { get; } = new();
        public int Layer { get; }
        public Point Origin { get; set; }
        public Vector Velocity { get; set; }
        public double Age { get; set; }
        public double Lifetime { get; set; }
        public double Phase { get; set; }
        public double Pulse { get; set; }
        public double Brightness { get; set; }

        public Star(int layer)
        {
            Layer = layer;
            Visual.Transform = Position;
        }
    }

    private sealed class Meteor
    {
        public DrawingVisual Visual { get; } = new() { Opacity = 0 };
        public TranslateTransform Position { get; } = new();
        public RotateTransform Rotation { get; } = new();
        public bool Active { get; set; }
        public Point Origin { get; set; }
        public Vector Velocity { get; set; }
        public double StartedAt { get; set; }
        public double Duration { get; set; }

        public Meteor()
        {
            var transform = new TransformGroup();
            transform.Children.Add(Rotation);
            transform.Children.Add(Position);
            Visual.Transform = transform;
        }
    }
}
