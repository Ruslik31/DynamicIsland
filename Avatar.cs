using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>Картинки аватарок с CDN Discord: одна на адрес, грузятся сами и перерисовывают тех, кто их ждёт.</summary>
static class AvatarImages
{
    const string AllowedHost = "cdn.discordapp.com";
    const int DecodeWidth = 128;

    static readonly Dictionary<string, BitmapImage> Cache = [];

    public static ImageSource? Get(string? url, Action ready)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != AllowedHost) return null;

        if (!Cache.TryGetValue(url, out BitmapImage? image))
        {
            try
            {
                image = new BitmapImage();
                image.BeginInit();
                image.UriSource = uri;
                image.DecodePixelWidth = DecodeWidth;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
            }
            catch (Exception ex)
            {
                App.Log(ex);
                return null;
            }
            Cache[url] = image;
            image.DownloadFailed += (_, _) => Cache.Remove(url);
        }

        if (!image.IsDownloading) return image.PixelWidth > 0 ? image : null;
        image.DownloadCompleted += (_, _) => ready();
        return null;
    }
}

/// <summary>Круглая аватарка с кольцом, которое вспыхивает, пока человек говорит.</summary>
public sealed class Avatar : FrameworkElement
{
    const double RingRoom = 2.4, RingWidth = 2.2;
    static readonly Color Speak = Color.FromRgb(0x30, 0xD1, 0x58);
    static readonly Brush RingBrush = Frozen(new SolidColorBrush(Speak));
    static readonly Typeface LetterFace = new("Segoe UI Semibold");

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(Avatar), new FrameworkPropertyMetadata(null, (d, _) => ((Avatar)d).Load()));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Avatar), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpeakingProperty = DependencyProperty.Register(
        nameof(Speaking), typeof(bool), typeof(Avatar), new FrameworkPropertyMetadata(false, (d, _) => ((Avatar)d).Pulse()));

    public static readonly DependencyProperty DimmedProperty = DependencyProperty.Register(
        nameof(Dimmed), typeof(bool), typeof(Avatar), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    readonly Spring _ring = new(0, 420, 16);
    readonly FrameLoop _loop;
    ImageSource? _image;

    public Avatar()
    {
        _loop = new FrameLoop(Advance);
        IsHitTestVisible = false;
    }

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool Speaking
    {
        get => (bool)GetValue(SpeakingProperty);
        set => SetValue(SpeakingProperty, value);
    }

    /// <summary>Приглушённая аватарка: человек отключил микрофон или наушники.</summary>
    public bool Dimmed
    {
        get => (bool)GetValue(DimmedProperty);
        set => SetValue(DimmedProperty, value);
    }

    void Load()
    {
        _image = AvatarImages.Get(Source, () => Dispatcher.BeginInvoke(() =>
        {
            _image = AvatarImages.Get(Source, () => { });
            InvalidateVisual();
        }));
        InvalidateVisual();
    }

    void Pulse()
    {
        if (!IsVisible)
        {
            _ring.Snap(Speaking ? 1 : 0);
            InvalidateVisual();
            return;
        }
        _ring.Target = Speaking ? 1 : 0;
        _loop.Start();
    }

    bool Advance(double dt)
    {
        bool moving = _ring.Advance(dt);
        InvalidateVisual();
        return moving;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - RingRoom;
        if (Dimmed) dc.PushOpacity(0.45);

        if (_image is { } image)
        {
            dc.PushClip(new EllipseGeometry(centre, radius, radius));
            dc.DrawImage(image, new Rect(centre.X - radius, centre.Y - radius, radius * 2, radius * 2));
            dc.Pop();
        }
        else
        {
            dc.DrawEllipse(Frozen(new SolidColorBrush(ColorFor(Label))), null, centre, radius, radius);
            DrawLetter(dc, centre, radius);
        }
        if (Dimmed) dc.Pop();

        double ring = Math.Clamp(_ring.Value, 0, 1.25);
        if (ring > 0.02)
        {
            var pen = new Pen(RingBrush, RingWidth * ring);
            double reach = size / 2 - RingWidth * ring / 2;
            dc.DrawEllipse(null, pen, centre, reach, reach);
        }
    }

    void DrawLetter(DrawingContext dc, Point centre, double radius)
    {
        string text = Label.Length > 0 ? char.ToUpperInvariant(Label[0]).ToString() : "?";
        var letter = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LetterFace,
            radius * 1.05, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(letter, new Point(centre.X - letter.Width / 2, centre.Y - letter.Height / 2));
    }

    static Color ColorFor(string label)
    {
        int hash = 17;
        foreach (char c in label) hash = hash * 31 + c;
        double hue = Math.Abs(hash % 360);
        return FromHsv(hue, 0.5, 0.62);
    }

    static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        (double r, double g, double b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
