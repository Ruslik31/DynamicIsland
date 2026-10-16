using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DynamicIsland;

sealed class PointerLyric : Window
{
    const double TextSize = 16;
    const double RoomWidth = 760, RoomHeight = 160;
    const double TextMargin = 48;
    const string Ellipsis = "…";
    const char FirstJoinedLetter = '֐', LastJoinedLetter = 'ࣿ';

    const double BelowPointer = 30, AbovePointer = 14, ScreenMargin = 12;
    const double FollowStiffness = 90, FollowDamping = 14;
    const double TiltPerSpeed = 0.006, MaxTilt = 6;
    const double DepthPerSpeed = 0.00012, MaxDepth = 0.14, DimPerDepth = 1.5;

    const double EnterRise = 9, EnterSeconds = 0.32, EnterStepSeconds = 0.014, EnterSpreadSeconds = 0.24;
    const double LeaveRise = 10, LeaveSeconds = 0.22;

    const double ShadowBlur = 14, ShadowDrop = 3, ShadowDown = 270, ShadowOpacity = 0.75;
    static readonly TimeSpan RestPoll = TimeSpan.FromMilliseconds(40);
    static readonly (int X, int Y) NowhereYet = (int.MinValue, int.MinValue);

    sealed class Stage : FrameworkElement
    {
        readonly VisualCollection _visuals;

        public Stage() => _visuals = new VisualCollection(this);

        public void Add(Visual visual) => _visuals.Add(visual);

        public void Remove(Visual visual) => _visuals.Remove(visual);

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];
    }

    sealed record Letter(DrawingVisual Visual, TranslateTransform Shift, double AppearAt);

    sealed record Line(string Text, ContainerVisual Visual, TranslateTransform Lift, Letter[] Letters, Size Size)
    {
        public double LeftAt { get; set; }
    }

    readonly Stage _stage = new();
    readonly ScaleTransform _depth = new(1, 1, RoomWidth / 2, RoomHeight / 2);
    readonly RotateTransform _tilt = new(0, RoomWidth / 2, RoomHeight / 2);
    readonly FrameLoop _loop;
    readonly DispatcherTimer _restPoll = new() { Interval = RestPoll };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Spring _followX = new(0, FollowStiffness, FollowDamping), _followY = new(0, FollowStiffness, FollowDamping);
    readonly Typeface _typeface;
    IntPtr _hwnd;
    Line? _line, _leaving;
    Point _restingPointer;
    (int X, int Y) _placed = NowhereYet;

    public PointerLyric(FontFamily font)
    {
        _typeface = new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = RoomWidth;
        Height = RoomHeight;
        Left = SystemParameters.VirtualScreenLeft - 2 * RoomWidth;
        Top = SystemParameters.VirtualScreenTop - 2 * RoomHeight;

        _stage.Effect = new DropShadowEffect
        {
            BlurRadius = ShadowBlur, ShadowDepth = ShadowDrop, Direction = ShadowDown, Opacity = ShadowOpacity, Color = Colors.Black,
        };
        _stage.RenderTransform = new TransformGroup { Children = { _depth, _tilt } };
        Content = _stage;
        _loop = new FrameLoop(Advance);
        _restPoll.Tick += (_, _) => WakeIfPointerMoved();
        Show();
    }

    public string Text => _line?.Text ?? "";

    double Now => _clock.Elapsed.TotalSeconds;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.LetClicksThrough(_hwnd);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _placed = NowhereYet;
    }

    public void ShowLine(string text)
    {
        if (text == Text) return;

        bool wasEmpty = _line == null && _leaving == null;
        if (_leaving != null) _stage.Remove(_leaving.Visual);
        _leaving = _line;
        if (_leaving != null) _leaving.LeftAt = Now;
        _line = text.Length > 0 ? Build(text) : null;

        if (_line != null)
        {
            Native.KeepOnTop(_hwnd);
            if (wasEmpty) SnapToPointer();
        }
        _restPoll.Stop();
        _loop.Start();
    }

    double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    void SnapToPointer()
    {
        double scale = Scale;
        Point spot = SpotNear(Native.CursorPosition, scale);
        _followX.Snap(spot.X);
        _followY.Snap(spot.Y);
        Lean(default);
        Place(scale);
    }

    Point SpotNear(Point pointer, double scale)
    {
        Size size = _line?.Size ?? _leaving?.Size ?? default;
        double halfWidth = size.Width / 2 * scale, halfHeight = size.Height / 2 * scale;
        double x = pointer.X, y = pointer.Y + BelowPointer * scale + halfHeight;

        Rect work = Native.WorkAreaAt(pointer);
        if (work.IsEmpty) return new Point(x, y);

        double margin = ScreenMargin * scale;
        if (y + halfHeight + margin > work.Bottom) y = pointer.Y - AbovePointer * scale - halfHeight;
        double reach = halfWidth + margin;
        x = work.Width > 2 * reach ? Math.Clamp(x, work.Left + reach, work.Right - reach) : work.Left + work.Width / 2;
        return new Point(x, y);
    }

    bool Advance(double dt)
    {
        if (_line == null && _leaving == null) return false;

        double now = Now, scale = Scale;
        Point spot = SpotNear(Native.CursorPosition, scale);
        _followX.Target = spot.X;
        _followY.Target = spot.Y;
        bool busy = _followX.Advance(dt) | _followY.Advance(dt);
        Place(scale);
        Lean(new Vector(_followX.Velocity, _followY.Velocity) / scale);

        if (_line != null) busy |= Appear(_line, now);
        if (_leaving != null) busy |= FadeAway(_leaving, now);
        if (busy) return true;

        if (_line != null)
        {
            _restingPointer = Native.CursorPosition;
            _restPoll.Start();
        }
        return false;
    }

    void WakeIfPointerMoved()
    {
        if (Native.CursorPosition == _restingPointer) return;
        _restPoll.Stop();
        _loop.Start();
    }

    void Place(double scale)
    {
        (int X, int Y) at = ((int)Math.Round(_followX.Value - RoomWidth * scale / 2), (int)Math.Round(_followY.Value - RoomHeight * scale / 2));
        if (at == _placed) return;
        _placed = at;
        Native.MoveTo(_hwnd, at.X, at.Y);
    }

    void Lean(Vector speed)
    {
        double depth = Math.Min(speed.Length * DepthPerSpeed, MaxDepth);
        _tilt.Angle = Math.Clamp(-speed.X * TiltPerSpeed, -MaxTilt, MaxTilt);
        _depth.ScaleX = _depth.ScaleY = 1 - depth;
        _stage.Opacity = 1 - depth * DimPerDepth;
    }

    static bool Appear(Line line, double now)
    {
        bool appearing = false;
        foreach (Letter letter in line.Letters)
        {
            double shown = Math.Clamp((now - letter.AppearAt) / EnterSeconds, 0, 1);
            double eased = 1 - Math.Pow(1 - shown, 3);
            letter.Visual.Opacity = eased;
            letter.Shift.Y = EnterRise * (1 - eased);
            appearing |= shown < 1;
        }
        return appearing;
    }

    bool FadeAway(Line line, double now)
    {
        double gone = Math.Min((now - line.LeftAt) / LeaveSeconds, 1);
        line.Visual.Opacity = 1 - gone;
        line.Lift.Y = -LeaveRise * gone * gone;
        if (gone < 1) return true;

        _stage.Remove(line.Visual);
        _leaving = null;
        return false;
    }

    Line Build(string text)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string shown = Fit(text, RoomWidth - 2 * TextMargin, pixelsPerDip);
        FormattedText whole = Format(shown, pixelsPerDip);
        var size = new Size(whole.WidthIncludingTrailingWhitespace, whole.Height);

        var lift = new TranslateTransform();
        var visual = new ContainerVisual
        {
            Offset = new Vector((RoomWidth - size.Width) / 2, (RoomHeight - size.Height) / 2),
            Transform = lift,
        };
        (int Start, int Length)[] pieces = [.. Split(shown)];
        double step = Math.Min(EnterStepSeconds, EnterSpreadSeconds / Math.Max(pieces.Length, 1)), start = Now;
        Letter[] letters = [.. pieces.Select((piece, i) => CreateLetter(visual, whole, shown, piece, start + i * step, pixelsPerDip))];
        _stage.Add(visual);
        return new Line(text, visual, lift, letters, size);
    }

    Letter CreateLetter(ContainerVisual line, FormattedText whole, string text, (int Start, int Length) piece, double appearAt, double pixelsPerDip)
    {
        Rect box = whole.BuildHighlightGeometry(new Point(), piece.Start, piece.Length)?.Bounds ?? new Rect();
        var shift = new TranslateTransform(0, EnterRise);
        var visual = new DrawingVisual { Transform = shift, Opacity = 0 };
        TextOptions.SetTextHintingMode(visual, TextHintingMode.Animated);
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawText(Format(text.Substring(piece.Start, piece.Length), pixelsPerDip), new Point(box.Left, 0));
        line.Children.Add(visual);
        return new Letter(visual, shift, appearAt);
    }

    static IEnumerable<(int Start, int Length)> Split(string text)
    {
        if (text.Any(letter => letter is >= FirstJoinedLetter and <= LastJoinedLetter)) return [(0, text.Length)];

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        return starts
            .Select((start, i) => (start, (i + 1 < starts.Length ? starts[i + 1] : text.Length) - start))
            .Where(letter => !char.IsWhiteSpace(text[letter.start]));
    }

    string Fit(string text, double maxWidth, double pixelsPerDip)
    {
        if (Format(text, pixelsPerDip).WidthIncludingTrailingWhitespace <= maxWidth) return text;

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        int kept = starts.Length - 1;
        string cut;
        do cut = text[..starts[kept]].TrimEnd() + Ellipsis;
        while (Format(cut, pixelsPerDip).WidthIncludingTrailingWhitespace > maxWidth && kept-- > 0);
        return cut;
    }

    FormattedText Format(string text, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, TextSize, Brushes.White, pixelsPerDip);
}
