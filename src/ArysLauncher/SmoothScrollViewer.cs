using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArysLauncher;

public sealed class SmoothScrollViewer : ScrollViewer
{
    private Window? owner;
    private bool moving;
    private double position;
    private double target;
    private double velocity;
    private double requestedOffset;
    private long previousTime;
    private TimeSpan lastFrame = TimeSpan.MinValue;

    public SmoothScrollViewer()
    {
        Loaded += (_, _) => owner = Window.GetWindow(this);
        Unloaded += (_, _) =>
        {
            Stop();
            owner = null;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                Stop();
            }
        };
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        var scrollLines = SystemParameters.WheelScrollLines;
        if (e.Handled || e.Delta == 0 || CanContentScroll || ScrollInfo == null || scrollLines == 0 ||
            (Keyboard.Modifiers & ModifierKeys.Control) != 0 || Mouse.Captured is not (null or ComboBox) ||
            LauncherMotion.GetReducedMotion(this) || !SystemParameters.ClientAreaAnimation)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }
        for (var source = e.OriginalSource as DependencyObject; source != null && source != this;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
        {
            if (source is ScrollViewer nested && nested.ScrollableHeight > 0 &&
                (e.Delta > 0 ? nested.VerticalOffset > 0 : nested.VerticalOffset < nested.ScrollableHeight))
            {
                base.OnPreviewMouseWheel(e);
                return;
            }
            if (source is ComboBox { IsKeyboardFocusWithin: true })
            {
                base.OnPreviewMouseWheel(e);
                return;
            }
        }
        if (ScrollableHeight <= 0)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }
        var amount = scrollLines < 0 ? Math.Max(1, ViewportHeight * 0.9) : scrollLines * 18.0;
        var step = -e.Delta / 120.0 * amount;
        if (!moving)
        {
            position = target = requestedOffset = VerticalOffset;
        }
        else if (Math.Sign(step) != Math.Sign(target - position))
        {
            target = position = VerticalOffset;
            velocity = 0;
        }
        var next = Math.Clamp(target + step, 0, ScrollableHeight);
        if (next == target && !moving)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }
        target = next;
        e.Handled = true;
        if (!moving)
        {
            previousTime = Stopwatch.GetTimestamp();
            lastFrame = TimeSpan.MinValue;
            moving = true;
            CompositionTarget.Rendering += Frame;
        }
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        Stop();
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        Stop();
        base.OnPreviewKeyDown(e);
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        if (!moving || !ReferenceEquals(e.OriginalSource, this))
        {
            return;
        }
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 ||
            e.VerticalChange != 0 && Math.Abs(VerticalOffset - requestedOffset) > 1)
        {
            Stop();
        }
    }

    private void Frame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (lastFrame == frame.RenderingTime)
            {
                return;
            }
            lastFrame = frame.RenderingTime;
        }
        if (!IsLoaded || !IsVisible || ScrollInfo is not { } scroll ||
            owner is { IsActive: false } || owner?.WindowState == WindowState.Minimized)
        {
            Stop();
            return;
        }
        if (LauncherMotion.GetReducedMotion(this) || !SystemParameters.ClientAreaAnimation)
        {
            Complete();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp(Stopwatch.GetElapsedTime(previousTime, now).TotalSeconds, 0, 0.1);
        previousTime = now;
        target = Math.Clamp(target, 0, ScrollableHeight);
        const double damping = 27;
        var displacement = position - target;
        var impulse = (velocity + damping * displacement) * elapsed;
        var decay = Math.Exp(-damping * elapsed);
        position = Math.Clamp(target + (displacement + impulse) * decay, 0, ScrollableHeight);
        velocity = (velocity - damping * impulse) * decay;
        if (Math.Abs(target - position) < 0.2 && Math.Abs(velocity) < 5)
        {
            Complete();
            return;
        }
        requestedOffset = position;
        scroll.SetVerticalOffset(position);
    }

    private void Complete()
    {
        var destination = Math.Clamp(target, 0, ScrollableHeight);
        Stop();
        ScrollInfo?.SetVerticalOffset(destination);
    }

    private void Stop()
    {
        if (!moving)
        {
            return;
        }
        CompositionTarget.Rendering -= Frame;
        moving = false;
        velocity = 0;
        previousTime = 0;
        lastFrame = TimeSpan.MinValue;
    }
}
