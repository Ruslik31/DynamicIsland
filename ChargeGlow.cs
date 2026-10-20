using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DynamicIsland;

public sealed class ChargeGlow : FrameworkElement
{
    const double FullCircle = 2 * Math.PI;
    const double RingThickness = 2, RingGap = 5;
    const double PulseSeconds = 0.9, PulseFloor = 0.35;
    const double BreathSeconds = 2.8, BreathDepth = 0.08;
    const double FullPresenceAt = 34;
    const double Softness = 13;
    const double FrontLag = 8, EndSpill = 14;
    const double Tail = 150, LitDepth = 60, LitFrontBlend = 7, LitTailBlend = 10;
    const double HotSpotBack = 0.85, HotSpotLength = 1.6, HotSpotHeight = 0.42;
    const double Whitening = 0.45;

    static readonly (double At, double Alpha)[] RingFade = [(0, 0.95), (0.3, 0.8), (0.62, 0.3), (1, 0.24)];
    static readonly (double At, double Alpha)[] TailFade = [(0, 0.04), (0.4, 0.2), (0.72, 0.72), (1, 1)];

    public static readonly DependencyProperty ReachProperty = DependencyProperty.Register(
        nameof(Reach), typeof(double), typeof(ChargeGlow),
        new FrameworkPropertyMetadata(0.0, (owner, _) => ((ChargeGlow)owner).DrawLight()));

    readonly FrameLoop _loop;
    readonly ContainerVisual _lit = new();
    readonly DrawingVisual _light = new()
    {
        Effect = new BlurEffect { Radius = Softness, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
    };
    readonly DrawingVisual _glass = new();
    Color _tint;
    Brush _track = Brushes.Transparent, _hotSpot = Brushes.Transparent;
    Pen? _ring, _rim;
    double _time;

    public ChargeGlow()
    {
        _loop = new FrameLoop(Advance);
        IsHitTestVisible = false;
        _lit.Children.Add(_light);
        AddVisualChild(_lit);
        AddVisualChild(_glass);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _loop.Start();
            else _loop.Stop();
        };
    }

    public double Reach
    {
        get => (double)GetValue(ReachProperty);
        set => SetValue(ReachProperty, value);
    }

    public bool Pulse { get; set; }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _lit : _glass;

    Rect Body => new(RingThickness + RingGap, RingThickness + RingGap,
        Math.Max(0, ActualWidth - 2 * (RingThickness + RingGap)), Math.Max(0, ActualHeight - 2 * (RingThickness + RingGap)));

    public bool Lights(UIElement part, double reach)
    {
        double middle = part.TranslatePoint(new Point(part.RenderSize.Width / 2, 0), this).X;
        double front = Front(Body, reach);
        return middle > front - LitDepth && middle < front;
    }

    public Brush Across(UIElement part, double reach, Color lit, Color aside)
    {
        double front = Front(Body, reach) - part.TranslatePoint(new Point(), this).X;
        double from = front - LitDepth - LitTailBlend, to = front + LitFrontBlend, span = to - from;
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(from, 0),
            EndPoint = new Point(to, 0),
            GradientStops =
            {
                new GradientStop(aside, 0),
                new GradientStop(lit, 2 * LitTailBlend / span),
                new GradientStop(lit, 1 - 2 * LitFrontBlend / span),
                new GradientStop(aside, 1),
            },
        };
        return Shades.Frozen(brush);
    }

    static double Front(Rect body, double reach)
    {
        double head = body.Left + body.Width * reach;
        double ending = Math.Clamp((head - body.Right + body.Height) / body.Height, 0, 1);
        return head - FrontLag + (FrontLag + EndSpill) * ending;
    }

    public void Tint(Color tint)
    {
        _tint = tint;
        _track = Shades.Frozen(new SolidColorBrush(tint.WithAlpha(0.12)));

        var hotSpot = new RadialGradientBrush();
        hotSpot.GradientStops.Add(new GradientStop(Whiten(tint, Whitening), 0));
        hotSpot.GradientStops.Add(new GradientStop(Whiten(tint, Whitening).WithAlpha(0), 1));
        _hotSpot = Shades.Frozen(hotSpot);

        var ring = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        foreach ((double at, double alpha) in RingFade) ring.GradientStops.Add(new GradientStop(tint.WithAlpha(alpha), at));
        _ring = Shades.Frozen(new Pen(Shades.Frozen(ring), RingThickness));

        _rim = Shades.Frozen(new Pen(Shades.Frozen(new LinearGradientBrush(
            Whiten(tint, Whitening).WithAlpha(0.24), tint.WithAlpha(0.06), 90)), 1));

        _time = 0;
        InvalidateVisual();
        DrawGlass();
        DrawLight();
    }

    bool Advance(double dt)
    {
        _time += dt;
        DrawLight();
        return true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Rect body = Body;
        _lit.Clip = new RectangleGeometry(body, body.Height / 2, body.Height / 2);
        DrawGlass();
        DrawLight();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double round = (h - RingThickness) / 2;
        dc.DrawRoundedRectangle(null, _ring, new Rect(RingThickness / 2, RingThickness / 2, w - RingThickness, h - RingThickness), round, round);
        Rect body = Body;
        dc.DrawRoundedRectangle(_track, null, body, body.Height / 2, body.Height / 2);
    }

    void DrawGlass()
    {
        Rect body = Body;
        using DrawingContext dc = _glass.RenderOpen();
        if (body.Width < 1 || body.Height < 1) return;
        double round = body.Height / 2 - 0.5;
        dc.DrawRoundedRectangle(null, _rim, new Rect(body.Left + 0.5, body.Top + 0.5, body.Width - 1, body.Height - 1), round, round);
    }

    void DrawLight()
    {
        Rect body = Body;
        using DrawingContext dc = _light.RenderOpen();
        double head = body.Left + body.Width * Reach;
        double presence = Math.Clamp((head - body.Left) / FullPresenceAt, 0, 1);
        if (presence <= 0 || body.Height < 1) return;

        double breath = Pulse
            ? PulseFloor + (1 - PulseFloor) * (0.5 + 0.5 * Math.Cos(FullCircle * _time / PulseSeconds))
            : 1 - BreathDepth * (0.5 - 0.5 * Math.Cos(FullCircle * _time / BreathSeconds));
        double spill = Softness / 2;
        double round = body.Height / 2 + spill;
        double front = Front(body, Reach);
        double start = Math.Min(front - Tail, body.Left - round);

        var fill = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(front - Tail, 0),
            EndPoint = new Point(front, 0),
        };
        foreach ((double at, double alpha) in TailFade) fill.GradientStops.Add(new GradientStop(_tint.WithAlpha(alpha), at));
        fill.Freeze();

        dc.PushOpacity(presence * (Pulse ? breath : 1));
        dc.DrawRoundedRectangle(fill, null, new Rect(start, body.Top - spill, Math.Max(0, front - start), 2 * round), round, round);
        dc.PushOpacity(Pulse ? 1 : breath);
        dc.DrawEllipse(_hotSpot, null, new Point(front - round * HotSpotBack, body.Top + body.Height / 2),
            round * HotSpotLength, body.Height * HotSpotHeight);
        dc.Pop();
        dc.Pop();
    }

    static Color Whiten(Color color, double share) => Color.FromRgb(
        (byte)(color.R + (255 - color.R) * share), (byte)(color.G + (255 - color.G) * share), (byte)(color.B + (255 - color.B) * share));
}
