using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ArysLauncher;

public static class LauncherMotion
{
    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.RegisterAttached("ReducedMotion", typeof(bool),
        typeof(LauncherMotion), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetReducedMotion(DependencyObject element)
    {
        return (bool)element.GetValue(ReducedMotionProperty);
    }

    public static void SetReducedMotion(DependencyObject element, bool value)
    {
        element.SetValue(ReducedMotionProperty, value);
    }

    public static void Reset(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.RenderTransform is TranslateTransform previous)
        {
            previous.BeginAnimation(TranslateTransform.YProperty, null);
        }
        element.Opacity = 1;
        element.RenderTransform = Transform.Identity;
    }

    public static void Enter(UIElement element, bool reducedMotion, int delayMilliseconds = 0)
    {
        Reset(element);
        if (reducedMotion)
        {
            return;
        }
        var translation = new TranslateTransform();
        element.RenderTransform = translation;
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        element.BeginAnimation(UIElement.OpacityProperty, Entrance(0, 1, delay, 210));
        translation.BeginAnimation(TranslateTransform.YProperty, Entrance(8, 0, delay, 290));
    }

    private static DoubleAnimationUsingKeyFrames Entrance(double from, double to, TimeSpan delay, int milliseconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        if (delay > TimeSpan.Zero)
        {
            animation.KeyFrames.Insert(0, new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        }
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(milliseconds)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        return animation;
    }
}
