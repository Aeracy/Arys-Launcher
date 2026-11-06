using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ArysLauncher;

public sealed class MemoryMeter : FrameworkElement
{
    private static readonly CubicEase EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly Pen RailOutline = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(140, 133, 106, 149)), 1));
    private static readonly DependencyProperty TransitionProperty = DependencyProperty.Register(nameof(Transition), typeof(double),
        typeof(MemoryMeter), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyPropertyKey HasShortfallPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasShortfall),
        typeof(bool), typeof(MemoryMeter), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HasShortfallProperty = HasShortfallPropertyKey.DependencyProperty;

    private readonly Brush systemBrush;
    private readonly Brush gameBrush;
    private readonly Brush pressureBrush;
    private readonly Brush reserveBrush;
    private RectangleGeometry? railClip;
    private double fromUsed;
    private double fromGame;
    private double targetUsed = double.NaN;
    private double targetGame = double.NaN;

    public bool HasShortfall => (bool)GetValue(HasShortfallProperty);
    private double Transition => (double)GetValue(TransitionProperty);

    public MemoryMeter()
    {
        var app = Application.Current;
        systemBrush = (Brush)app.FindResource("MemorySystemBrush");
        gameBrush = (Brush)app.FindResource("MemoryGameBrush");
        pressureBrush = (Brush)app.FindResource("MemoryPressureBrush");
        reserveBrush = (Brush)app.FindResource("MemoryReserveBrush");
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                CompleteTransition();
            }
        };
    }

    internal void Update(MemoryBudget budget, bool reducedMotion)
    {
        if (HasShortfall != budget.HasShortfall)
        {
            SetValue(HasShortfallPropertyKey, budget.HasShortfall);
        }
        var total = Math.Max(1, budget.Memory.TotalGiB);
        var used = Math.Clamp(budget.Memory.UsedGiB / total, 0, 1);
        var game = Math.Clamp(budget.GameGiB / total, 0, 1 - used);
        var immediate = reducedMotion || !IsVisible || double.IsNaN(targetUsed);
        var threshold = immediate ? 0 : 0.5 / Math.Max(1, ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (Math.Abs(targetUsed - used) <= threshold && Math.Abs(targetGame - game) <= threshold)
        {
            if (immediate && Transition < 1)
            {
                CompleteTransition();
            }
            return;
        }

        (fromUsed, fromGame) = CurrentSegments();
        targetUsed = used;
        targetGame = game;
        if (immediate)
        {
            CompleteTransition();
            return;
        }
        BeginAnimation(TransitionProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop
        });
        InvalidateVisual();
    }

    internal void CompleteTransition()
    {
        BeginAnimation(TransitionProperty, null);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var height = Math.Min(14, ActualHeight);
        if (height <= 4 || ActualWidth <= 4)
        {
            return;
        }
        var rail = new Rect(0.5, (ActualHeight - height) / 2, ActualWidth - 1, height);
        drawing.DrawRoundedRectangle(reserveBrush, RailOutline, rail, height / 2, height / 2);
        var inner = rail;
        inner.Inflate(-1.5, -1.5);
        if (railClip == null || railClip.Rect != inner)
        {
            railClip = Freeze(new RectangleGeometry(inner, inner.Height / 2, inner.Height / 2));
        }
        var (used, game) = CurrentSegments();
        var usedWidth = Math.Clamp(used * inner.Width, 0, inner.Width);
        var gameWidth = Math.Clamp(game * inner.Width, 0, inner.Width - usedWidth);
        drawing.PushClip(railClip);
        if (gameWidth > 0)
        {
            var gameStart = Math.Max(0, usedWidth - 1);
            drawing.DrawRectangle(HasShortfall ? pressureBrush : gameBrush, null,
                new Rect(inner.X + gameStart, inner.Y, usedWidth + gameWidth - gameStart, inner.Height));
        }
        if (usedWidth > 0)
        {
            drawing.DrawRectangle(systemBrush, null, new Rect(inner.X, inner.Y, usedWidth, inner.Height));
        }
        drawing.Pop();
    }

    private (double Used, double Game) CurrentSegments()
    {
        if (double.IsNaN(targetUsed))
        {
            return (0, 0);
        }
        var progress = Transition;
        return (fromUsed + (targetUsed - fromUsed) * progress, fromGame + (targetGame - fromGame) * progress);
    }

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
