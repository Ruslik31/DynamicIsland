using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

public enum VoiceGlyph
{
    Mic, MicOff, Headset, HeadsetOff, Screen,
}

/// <summary>Микрофон, наушники и трансляция, нарисованные на сетке 24 × 24; «выключено» перечёркнуто чертой.</summary>
public sealed class VoiceIcon : FrameworkElement
{
    const double Grid = 24, Stroke = 1.9, Knockout = 4.2;

    static readonly Geometry MicBody = Frozen(new RectangleGeometry(new Rect(9, 2.5, 6, 11.5), 3, 3));
    static readonly Geometry MicHolder = Frozen(Parse("M5.6,11 A6.4,6.4 0 0 0 18.4,11 M12,17.4 V21 M8.6,21 H15.4"));
    static readonly Geometry HeadBand = Frozen(Parse("M4.6,15 V12.4 A7.4,7.4 0 0 1 19.4,12.4 V15"));
    static readonly Geometry[] Cups =
    [
        Frozen(new RectangleGeometry(new Rect(3, 13.4, 4.6, 7.2), 2, 2)),
        Frozen(new RectangleGeometry(new Rect(16.4, 13.4, 4.6, 7.2), 2, 2)),
    ];
    static readonly Geometry ScreenFrame = Frozen(new RectangleGeometry(new Rect(3, 4.5, 18, 12), 2.4, 2.4));
    static readonly Geometry ScreenStand = Frozen(Parse("M8.6,20 H15.4 M12,16.5 V20"));
    static readonly Geometry Slash = Frozen(Parse("M4.2,4.2 L19.8,19.8"));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(VoiceGlyph), typeof(VoiceIcon),
        new FrameworkPropertyMetadata(VoiceGlyph.Mic, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(VoiceIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public VoiceIcon() => IsHitTestVisible = false;

    public VoiceGlyph Kind
    {
        get => (VoiceGlyph)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        Draw(dc, Kind, Fill, (ActualWidth - size) / 2, (ActualHeight - size) / 2, size);
    }

    /// <summary>Рисует значок в квадрате со стороной <paramref name="size"/>; мелким значкам нужна толще линия.</summary>
    internal static void Draw(DrawingContext dc, VoiceGlyph kind, Brush fill, double left, double top, double size, double stroke = Stroke)
    {
        double scale = size / Grid;
        if (scale <= 0) return;

        dc.PushTransform(new TranslateTransform(left, top));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(fill, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        switch (kind)
        {
            case VoiceGlyph.Mic:
            case VoiceGlyph.MicOff:
                dc.DrawGeometry(fill, null, MicBody);
                dc.DrawGeometry(null, pen, MicHolder);
                break;
            case VoiceGlyph.Headset:
            case VoiceGlyph.HeadsetOff:
                dc.DrawGeometry(null, pen, HeadBand);
                foreach (Geometry cup in Cups) dc.DrawGeometry(fill, null, cup);
                break;
            case VoiceGlyph.Screen:
                dc.DrawGeometry(null, pen, ScreenFrame);
                dc.DrawGeometry(null, pen, ScreenStand);
                break;
        }

        if (kind is VoiceGlyph.MicOff or VoiceGlyph.HeadsetOff)
        {
            double wide = Knockout + (stroke - Stroke) * 2;
            dc.DrawGeometry(null, new Pen(Brushes.Black, wide) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Slash);
            dc.DrawGeometry(null, pen, Slash);
        }
        dc.Pop();
        dc.Pop();
    }

    static Geometry Parse(string path) => Geometry.Parse(path);

    static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
