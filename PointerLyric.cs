using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DynamicIsland;

sealed class PointerLyric
{
    const double TextSize = 17;
    const double MaxTextWidth = 460, RoomPadding = 90, RoomHeight = 220;
    const double RoomWidth = MaxTextWidth + 2 * RoomPadding;
    const string Ellipsis = "…";
    const char FirstJoinedLetter = '֐', LastJoinedLetter = 'ࣿ';

    const double SideGap = 34, BelowPointer = 8, ScreenMargin = 12, SideRoomToReturn = 160;
    const double FollowStiffness = 60, FollowDamping = 12;
    const double SideStiffness = 45, SideDamping = 12;
    const double ShownStiffness = 110, ShownDamping = 21, HiddenEnough = 0.01;

    const double FocalLength = 620, DimDepth = 820, NearestDepth = -260;
    const double SideYaw = 14, YawPerSpeed = 0.014, MaxSpeedYaw = 26;
    const double PitchPerSpeed = 0.014, MaxPitch = 18;
    const double RollPerSpeed = 0.004, MaxRoll = 5;
    const double DepthPerSpeed = 0.06, MaxSpeedDepth = 140;
    const double TrailSeconds = 0.045, MaxTrail = 60, TrailCurve = 1.6;
    const double SwingDepth = 220, HiddenDepth = 200, ThinnestLetter = 0.2;

    const double DriftRise = 3.5, DriftRiseSeconds = 3.7, DriftDepth = 26, DriftDepthSeconds = 5.3, DriftYaw = 4, DriftYawSeconds = 6.1;
    const double RippleDepth = 6, RippleRise = 1.2, RippleLength = 190, RippleSeconds = 2.6, RippleRiseStretch = 0.7;

    const double EnterDepth = 300, EnterRise = 14, EnterSeconds = 0.62, EnterStepSeconds = 0.024, EnterSpreadSeconds = 0.36;
    const double LeaveDepth = 260, LeaveRise = 18, LeaveSeconds = 0.44;
    const double CalmFrameSeconds = 1.0 / 30;

    const double ShadowBlur = 12, ShadowDrop = 2, ShadowDown = 270, ShadowOpacity = 0.7;
    static readonly TimeSpan HiddenPoll = TimeSpan.FromMilliseconds(60);
    static readonly (int X, int Y, int Width, int Height) NowhereYet = (int.MinValue, int.MinValue, 0, 0);

    sealed class Stage : FrameworkElement
    {
        readonly VisualCollection _visuals;

        public Stage() => _visuals = new VisualCollection(this);

        public void Add(Visual visual) => _visuals.Add(visual);

        public void Remove(Visual visual) => _visuals.Remove(visual);

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];
    }

    sealed record Letter(DrawingVisual Visual, MatrixTransform Place, double X, double AppearAt);

    sealed record Line(string Text, ContainerVisual Visual, Letter[] Letters, double Width)
    {
        public double LeftAt { get; set; } = double.NaN;

        public bool IsLeaving => !double.IsNaN(LeftAt);
    }

    readonly record struct Pose(double Side, double Yaw, double Pitch, double Roll, double Depth, double Rise, Vector Speed, double Shown);

    readonly HwndSource _source;
    readonly Stage _stage = new();
    readonly FrameLoop _loop;
    readonly DispatcherTimer _hiddenPoll = new() { Interval = HiddenPoll };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Spring _followX = new(0, FollowStiffness, FollowDamping), _followY = new(0, FollowStiffness, FollowDamping);
    readonly Spring _side = new(1, SideStiffness, SideDamping);
    readonly Spring _shown = new(1, ShownStiffness, ShownDamping);
    readonly Func<bool> _isPointerOverIsland;
    readonly Typeface _typeface;
    Line? _line, _leaving;
    int _sideTarget = 1;
    double _calmFor;
    (int X, int Y, int Width, int Height) _placed = NowhereYet;

    public PointerLyric(FontFamily font, Func<bool> isPointerOverIsland)
    {
        _typeface = new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _isPointerOverIsland = isPointerOverIsland;

        _stage.Effect = new DropShadowEffect
        {
            BlurRadius = ShadowBlur, ShadowDepth = ShadowDrop, Direction = ShadowDown, Opacity = ShadowOpacity, Color = Colors.Black,
        };
        HwndSourceParameters parameters = Native.PassThroughOverlay(nameof(PointerLyric), (int)RoomWidth, (int)RoomHeight);
        parameters.PositionX = (int)(SystemParameters.VirtualScreenLeft - 2 * RoomWidth);
        parameters.PositionY = (int)(SystemParameters.VirtualScreenTop - 2 * RoomHeight);
        _source = new HwndSource(parameters);
        _source.AddHook(Native.PassHitsThrough);
        _source.CompositionTarget.BackgroundColor = Colors.Transparent;
        _source.RootVisual = _stage;

        _loop = new FrameLoop(Advance);
        _hiddenPoll.Tick += (_, _) => WakeIfRevealed();
    }

    public IntPtr Handle => _source.Handle;

    public string Text => _line?.Text ?? "";

    double Now => _clock.Elapsed.TotalSeconds;

    double Scale => _source.CompositionTarget.TransformToDevice.M11;

    public void ShowLine(string text)
    {
        if (text == Text) return;

        bool wasEmpty = _line == null && _leaving == null;
        if (_leaving != null) _stage.Remove(_leaving.Visual);
        _leaving = _line;
        if (_leaving != null) _leaving.LeftAt = Now;
        if (wasEmpty && text.Length > 0) Settle(Native.CursorPosition, text);
        _line = text.Length > 0 ? Build(text) : null;

        _hiddenPoll.Stop();
        _loop.Start();
    }

    void Settle(Point pointer, string text)
    {
        _shown.Snap(_isPointerOverIsland() ? 0 : 1);
        double width = Math.Min(Measure(text, Scale), MaxTextWidth);
        ChooseSide(pointer, Scale, width, true);
        SnapTo(pointer, width);
    }

    void SnapTo(Point pointer, double width)
    {
        double scale = Scale;
        Point spot = SpotNear(pointer, scale, width);
        _followX.Snap(spot.X);
        _followY.Snap(spot.Y);
        _side.Snap(_sideTarget);
        Place(scale);
    }

    double TextWidth => _line?.Width ?? _leaving?.Width ?? 0;

    void ChooseSide(Point pointer, double scale, double width, bool snap)
    {
        Rect work = Native.WorkAreaAt(pointer);
        if (work.IsEmpty) return;

        double need = (SideGap + width + ScreenMargin) * scale, toReturn = SideRoomToReturn * scale;
        double right = work.Right - pointer.X, left = pointer.X - work.Left;
        if (snap) _sideTarget = right >= need || right >= left ? 1 : -1;
        else if (_sideTarget > 0 && right < need && left > right) _sideTarget = -1;
        else if (_sideTarget < 0 && (right >= need + toReturn || (left < need && right > left))) _sideTarget = 1;
        _side.Target = _sideTarget;
    }

    Point SpotNear(Point pointer, double scale, double width)
    {
        double halfHeight = TextSize * scale;
        double x = pointer.X + _sideTarget * (SideGap + width / 2) * scale, y = pointer.Y + BelowPointer * scale;

        Rect work = Native.WorkAreaAt(pointer);
        if (work.IsEmpty) return new Point(x, y);

        double margin = ScreenMargin * scale, reach = width / 2 * scale + margin;
        x = work.Width > 2 * reach ? Math.Clamp(x, work.Left + reach, work.Right - reach) : work.Left + work.Width / 2;
        y = Math.Clamp(y, work.Top + halfHeight + margin, Math.Max(work.Top + halfHeight + margin, work.Bottom - halfHeight - margin));
        return new Point(x, y);
    }

    bool Advance(double dt)
    {
        if (_line == null && _leaving == null) return false;

        double now = Now, scale = Scale, width = TextWidth;
        Point pointer = Native.CursorPosition;
        bool hide = _isPointerOverIsland();
        _shown.Target = hide ? 0 : 1;
        bool busy = _shown.Advance(dt);

        ChooseSide(pointer, scale, width, false);
        Point spot = SpotNear(pointer, scale, width);
        _followX.Target = spot.X;
        _followY.Target = spot.Y;
        busy |= _followX.Advance(dt) | _followY.Advance(dt) | _side.Advance(dt);

        if (_leaving != null && now - _leaving.LeftAt >= LeaveSeconds)
        {
            _stage.Remove(_leaving.Visual);
            _leaving = null;
        }
        busy |= _leaving != null || (_line != null && IsAppearing(_line, now));

        bool hidden = hide && _shown.Value < HiddenEnough;
        if (!busy && !hidden)
        {
            _calmFor += dt;
            if (_calmFor < CalmFrameSeconds) return true;
        }
        _calmFor = 0;

        Place(scale);
        Pose pose = PoseAt(now, new Vector(_followX.Velocity, _followY.Velocity) / scale);
        if (_line != null) Arrange(_line, pose, now);
        if (_leaving != null) Arrange(_leaving, pose, now);

        if (hidden && !busy)
        {
            if (_line != null) _hiddenPoll.Start();
            return false;
        }
        return _line != null || _leaving != null;
    }

    void WakeIfRevealed()
    {
        if (_isPointerOverIsland()) return;
        _hiddenPoll.Stop();
        SnapTo(Native.CursorPosition, TextWidth);
        _loop.Start();
    }

    void Place(double scale)
    {
        int width = (int)Math.Round(RoomWidth * scale), height = (int)Math.Round(RoomHeight * scale);
        (int X, int Y, int Width, int Height) at = ((int)Math.Round(_followX.Value - width / 2.0), (int)Math.Round(_followY.Value - height / 2.0), width, height);
        if (at == _placed) return;
        _placed = at;
        Native.MoveAndSize(_source.Handle, at.X, at.Y, at.Width, at.Height);
    }

    Pose PoseAt(double now, Vector speed)
    {
        double side = _side.Value, shown = _shown.Value;
        double yaw = side * SideYaw + Math.Clamp(speed.X * YawPerSpeed, -MaxSpeedYaw, MaxSpeedYaw) + DriftYaw * Wave(now, DriftYawSeconds);
        double pitch = Math.Clamp(-speed.Y * PitchPerSpeed, -MaxPitch, MaxPitch);
        double roll = Math.Clamp(-speed.X * RollPerSpeed, -MaxRoll, MaxRoll);
        double depth = Math.Min(speed.Length * DepthPerSpeed, MaxSpeedDepth) + SwingDepth * (1 - side * side)
            + HiddenDepth * (1 - shown) + DriftDepth * Wave(now, DriftDepthSeconds);
        return new Pose(side, Radians(yaw), Radians(pitch), Radians(roll), depth, DriftRise * Wave(now, DriftRiseSeconds), speed, shown);
    }

    static void Arrange(Line line, Pose pose, double now)
    {
        double cosYaw = Math.Cos(pose.Yaw), sinYaw = Math.Sin(pose.Yaw);
        double cosPitch = Math.Cos(pose.Pitch), sinPitch = Math.Sin(pose.Pitch);
        double cosRoll = Math.Cos(pose.Roll), sinRoll = Math.Sin(pose.Roll);
        double gone = line.IsLeaving ? Math.Min((now - line.LeftAt) / LeaveSeconds, 1) : 0;
        double ripplePhase = 2 * Math.PI * now / RippleSeconds;

        foreach (Letter letter in line.Letters)
        {
            double appeared = 1 - Math.Pow(1 - Math.Clamp((now - letter.AppearAt) / EnterSeconds, 0, 1), 3);
            double fromPointer = Math.Clamp((pose.Side * letter.X + line.Width / 2) / Math.Max(line.Width, 1), 0, 1);
            double ripple = 2 * Math.PI * letter.X / RippleLength - ripplePhase;

            double x = letter.X, y = pose.Rise + RippleRise * Math.Sin(ripple * RippleRiseStretch);
            double turnedX = x * cosYaw, turnedZ = x * sinYaw;
            double tiltedY = y * cosPitch - turnedZ * sinPitch, tiltedZ = y * sinPitch + turnedZ * cosPitch;
            double depth = Math.Max(tiltedZ + RippleDepth * Math.Sin(ripple) + pose.Depth + EnterDepth * (1 - appeared) + LeaveDepth * gone * gone, NearestDepth);
            tiltedY += EnterRise * (1 - appeared) - LeaveRise * gone;

            double near = FocalLength / (FocalLength + depth);
            Vector trail = -pose.Speed * TrailSeconds * Math.Pow(fromPointer, TrailCurve);
            if (trail.Length > MaxTrail) trail *= MaxTrail / trail.Length;
            double flatX = turnedX * near + trail.X, flatY = tiltedY * near + trail.Y;

            double scaleX = near * Math.Max(cosYaw, ThinnestLetter), scaleY = near * cosPitch;
            letter.Place.Matrix = new Matrix(
                scaleX * cosRoll, scaleX * sinRoll, -scaleY * sinRoll, scaleY * cosRoll,
                RoomWidth / 2 + flatX * cosRoll - flatY * sinRoll, RoomHeight / 2 + flatX * sinRoll + flatY * cosRoll);
            letter.Visual.Opacity = appeared * (1 - gone) * pose.Shown * Math.Clamp(1 - depth / DimDepth, 0, 1);
        }
    }

    static bool IsAppearing(Line line, double now) => line.Letters.Length > 0 && now - line.Letters.Max(letter => letter.AppearAt) < EnterSeconds;

    static double Wave(double now, double seconds) => Math.Sin(2 * Math.PI * now / seconds);

    static double Radians(double degrees) => degrees * Math.PI / 180;

    Line Build(string text)
    {
        double pixelsPerDip = Scale;
        string shown = Fit(text, MaxTextWidth, pixelsPerDip);
        FormattedText whole = Format(shown, pixelsPerDip);
        double width = whole.WidthIncludingTrailingWhitespace, height = whole.Height;

        var visual = new ContainerVisual();
        (int Start, int Length)[] pieces = [.. Split(shown)];
        double step = Math.Min(EnterStepSeconds, EnterSpreadSeconds / Math.Max(pieces.Length, 1)), start = Now;
        Letter[] letters = [.. pieces.Select((piece, i) =>
        {
            int fromPointer = _sideTarget > 0 ? i : pieces.Length - 1 - i;
            return CreateLetter(visual, whole, shown, piece, width, height, start + fromPointer * step, pixelsPerDip);
        })];
        _stage.Add(visual);
        return new Line(text, visual, letters, width);
    }

    Letter CreateLetter(ContainerVisual line, FormattedText whole, string text, (int Start, int Length) piece,
        double lineWidth, double lineHeight, double appearAt, double pixelsPerDip)
    {
        Rect box = whole.BuildHighlightGeometry(new Point(), piece.Start, piece.Length)?.Bounds ?? new Rect();
        var place = new MatrixTransform(new Matrix(1, 0, 0, 1, -RoomWidth, -RoomHeight));
        var visual = new DrawingVisual { Transform = place, Opacity = 0 };
        TextOptions.SetTextHintingMode(visual, TextHintingMode.Animated);
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawText(Format(text.Substring(piece.Start, piece.Length), pixelsPerDip), new Point(-box.Width / 2, -lineHeight / 2));
        line.Children.Add(visual);
        return new Letter(visual, place, box.Left + box.Width / 2 - lineWidth / 2, appearAt);
    }

    static IEnumerable<(int Start, int Length)> Split(string text)
    {
        if (text.Any(letter => letter is >= FirstJoinedLetter and <= LastJoinedLetter)) return [(0, text.Length)];

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        return starts
            .Select((start, i) => (start, (i + 1 < starts.Length ? starts[i + 1] : text.Length) - start))
            .Where(letter => !char.IsWhiteSpace(text[letter.start]));
    }

    double Measure(string text, double pixelsPerDip) => Format(text, pixelsPerDip).WidthIncludingTrailingWhitespace;

    string Fit(string text, double maxWidth, double pixelsPerDip)
    {
        if (Measure(text, pixelsPerDip) <= maxWidth) return text;

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        int kept = starts.Length - 1;
        string cut;
        do cut = text[..starts[kept]].TrimEnd() + Ellipsis;
        while (Measure(cut, pixelsPerDip) > maxWidth && kept-- > 0);
        return cut;
    }

    FormattedText Format(string text, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, TextSize, Brushes.White, pixelsPerDip);
}
