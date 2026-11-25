using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArysLauncher;

public partial class ProfileImageEditorWindow : Window
{
    private readonly ProfileImageSource source;
    private readonly bool avatar;
    private double panX;
    private double panY;
    private double scale = 1;
    private Rect crop;
    private Point? pointer;

    internal PlayerProfileImage? Selection { get; private set; }

    internal ProfileImageEditorWindow(ProfileImageSource source, bool avatar)
    {
        InitializeComponent();
        this.source = source;
        this.avatar = avatar;
        SourceImage.Source = source.Image;
        Title = Heading.Text = LauncherText.Get(avatar ? "ProfileEditAvatar" : "ProfileEditBanner");
        Language = LauncherText.Language;
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += (_, _) =>
        {
            Redraw();
            Viewport.Focus();
            LauncherMotion.Enter(EditorSurface, LauncherMotion.GetReducedMotion(Owner));
        };
    }

    private void Redraw()
    {
        if (source == null || Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1)
        {
            return;
        }
        var ratio = avatar ? 1 : 4;
        var width = Math.Max(1, Math.Min(Viewport.ActualWidth - 40, (Viewport.ActualHeight - 40) * ratio));
        crop = new Rect((Viewport.ActualWidth - width) / 2, (Viewport.ActualHeight - width / ratio) / 2, width, width / ratio);
        scale = Math.Max(crop.Width / source.Image.PixelWidth, crop.Height / source.Image.PixelHeight) * ZoomSlider.Value;
        var maxX = Math.Max(0, (source.Image.PixelWidth - crop.Width / scale) / 2);
        var maxY = Math.Max(0, (source.Image.PixelHeight - crop.Height / scale) / 2);
        panX = Math.Clamp(panX, -maxX, maxX);
        panY = Math.Clamp(panY, -maxY, maxY);
        SourceImage.Width = source.Image.PixelWidth * scale;
        SourceImage.Height = source.Image.PixelHeight * scale;
        Canvas.SetLeft(SourceImage, Viewport.ActualWidth / 2 - SourceImage.Width / 2 + panX * scale);
        Canvas.SetTop(SourceImage, Viewport.ActualHeight / 2 - SourceImage.Height / 2 + panY * scale);
        Canvas.SetLeft(CropFrame, crop.X);
        Canvas.SetTop(CropFrame, crop.Y);
        CropFrame.Width = crop.Width;
        CropFrame.Height = crop.Height;
        CropShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight)), new RectangleGeometry(crop));
        var selection = CurrentCrop();
        CropDetails.Text = LauncherText.Get("ProfileCropDimensions", selection.Width, selection.Height, source.Image.PixelWidth, source.Image.PixelHeight);
    }

    private PlayerProfileImage CurrentCrop()
    {
        var width = Math.Clamp((int)Math.Floor(crop.Width / scale + 0.5), 1, source.Image.PixelWidth);
        var height = Math.Clamp((int)Math.Floor(crop.Height / scale + 0.5), 1, source.Image.PixelHeight);
        var x = Math.Clamp((int)Math.Floor((source.Image.PixelWidth - width) / 2.0 - panX + 0.5), 0, source.Image.PixelWidth - width);
        var y = Math.Clamp((int)Math.Floor((source.Image.PixelHeight - height) / 2.0 - panY + 0.5), 0, source.Image.PixelHeight - height);
        return new PlayerProfileImage(source.Hash, x, y, width, height);
    }

    private void Viewport_Down(object sender, MouseButtonEventArgs e)
    {
        Viewport.Focus();
        pointer = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_Move(object sender, MouseEventArgs e)
    {
        if (pointer is not { } previous || !Viewport.IsMouseCaptured)
        {
            return;
        }
        var current = e.GetPosition(Viewport);
        panX += (current.X - previous.X) / scale;
        panY += (current.Y - previous.Y) / scale;
        pointer = current;
        Redraw();
    }

    private void Viewport_Up(object sender, MouseButtonEventArgs e)
    {
        Viewport.ReleaseMouseCapture();
    }

    private void Viewport_LostCapture(object sender, MouseEventArgs e)
    {
        pointer = null;
    }

    private void Viewport_Wheel(object sender, MouseWheelEventArgs e)
    {
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value * Math.Exp(e.Delta / 120.0 * 0.12), 1, 6);
        e.Handled = true;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Redraw();
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Redraw();
    }

    private void Center_Click(object sender, RoutedEventArgs e)
    {
        panX = panY = 0;
        ZoomSlider.Value = 1;
        Redraw();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Selection = CurrentCrop();
        DialogResult = true;
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        var amount = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: panX -= amount; break;
            case Key.Right: panX += amount; break;
            case Key.Up: panY -= amount; break;
            case Key.Down: panY += amount; break;
            default: return;
        }
        e.Handled = true;
        Redraw();
    }
}
