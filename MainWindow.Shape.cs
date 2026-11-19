using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DynamicIsland;

public partial class MainWindow
{
    const double MinPillSize = 24;
    const double MinScale = 0.01;
    const double ShadowFadeHeight = 50;
    const double OffscreenMargin = 30;
    const double PressedScale = 0.93, PressedOpenScale = 0.975, HoverScale = 1.07;
    const double BubbleGap = 7;
    const double BubbleTuckFactor = 1.6;
    const double BubbleVisibleSplit = 0.01, BubbleClickableSplit = 0.75;
    const double RecordBubbleWidth = 78, TimerBubbleWidth = 78, ShelfBubbleWidth = 54, MusicBubbleWidth = 66, MusicMiniWidth = 34, UpdateBubbleWidth = 36;
    const double BubbleOverlap = 11;
    const double ShelfBubblePadding = 13, ShelfBubblePaddingWithTimer = 10;
    const double MinViewScale = 0.5, MaxViewScale = 1.15;
    const double MotionBlurSpeed = 650;
    const double MaxMotionBlur = 4, MinMotionBlur = 0.1;
    const double CacheScaleTolerance = 0.001;
    const double ShadowReach = 100;
    static readonly TimeSpan GlassTime = TimeSpan.FromMilliseconds(300);

    readonly Spring _width = new(34), _height = new(34), _radius = new(17);
    readonly Spring _scale = new(1, 320, 20), _offsetY = new(0, 260, 26), _moveX = new(Settings.Position, 300, 30);
    readonly Spring _userScale = new(Settings.Scale / 100.0, 240, 26), _topGap = new(Settings.Gap, 240, 26);
    readonly Spring _bubbleSplit = new(0), _bubbleScale = new(1, 320, 20);
    readonly Spring _lyricSnapX = new(0, 200, 28), _lyricSnapY = new(0, 200, 28);
    readonly Spring _musicBubbleWidth = new(Settings.DiscordMusicMini ? MusicMiniWidth : MusicBubbleWidth, 260, 24), _bubbleDrag = new(0, 700, 50), _bubbleDragY = new(0, 700, 50);
    readonly Spring _bubbleMusic = new(0, 260, 24), _bubbleRecord = new(0, 260, 24), _bubbleTimer = new(0, 260, 24), _bubbleShelf = new(0, 260, 24), _bubbleUpdate = new(0, 260, 24), _shelfBubbleWidth = new(ShelfBubbleWidth, 260, 24);
    readonly BlurEffect _motionBlur = new() { Radius = 0, RenderingBias = RenderingBias.Performance };
    readonly FrameLoop _shapeLoop;
    readonly Glass _glass;
    readonly Spring[] _springs;
    Rect _clipRect = Rect.Empty;
    double _clipRadius, _baseLeft;
    bool _morphing, _growing;
    double _growBubbleX;
    Rect _pillRect = Rect.Empty, _bubbleRect = Rect.Empty, _growFrom = Rect.Empty;
    readonly Spring _bubbleGrow = new(0, 300, 24), _islandShift = new(0, 300, 24), _normalTransition = new(Settings.DiscordMusicState == DiscordMusicState.Normal ? 1 : 0, 240, 24), _bottomMusicScale = new(1, 320, 20);
    bool _bottomMusicHovered, _bottomMusicPressed, _bottomMusicDragging, _bottomMusicDropToCompact;
    double _bottomMusicDownX, _bottomMusicDownY;
    Rect _bottomMusicRect = Rect.Empty;

    void UpdateTargets()
    {
        PillShape shape = ShapeOf(_view);
        bool compact = shape.Height < CompactMaxHeight;
        double pull = RubberBand(_pull, MaxPull);
        bool dragging = _grab is Grab.Pull or Grab.Move;
        _width.Target = shape.Width + pull * PullWidening;
        _height.Target = shape.Height + pull;
        _radius.Target = shape.Radius + pull * PullRounding;
        if (MediaSpots.TryGetValue(_view, out MediaSpot spot))
        {
            _coverTop.Target = spot.CoverTop + pull / 2;
            _barsCenterY.Target = spot.BarsCenterY + pull / 2;
            AimCoverAtWholePixels(spot, shape.Width);
        }
        if (dragging) TuneDragSprings(true);
        UpdateBubbleTargets(compact);
        SyncEq();
        _offsetY.Target = (_hiddenByFullscreen || _sentAway) && !_ringing ? -(shape.Height + OffscreenMargin + Settings.Gap * 100.0 / Settings.Scale) : 0;
        _scale.Target = dragging ? 1 : _pressed ? (compact ? PressedScale : PressedOpenScale) : _hovered && compact ? HoverScale : 1;
        _bubbleScale.Target = _bubblePressed ? PressedScale : _bubbleHovered ? HoverScale : 1;
        _bottomMusicScale.Target = _bottomMusicPressed ? PressedScale : _bottomMusicHovered ? HoverScale : 1;
        StartShapeLoop();
    }

    void UpdateBubbleTargets(bool compact)
    {
        bool music = WantsMusicBubble();
        bool record = _obs.Recording && _view != View.Record;
        bool timer = _countdown.IsActive && _view != View.Timer, shelf = _shelf.Items.Count > 0;
        bool update = _updater.State == Updater.Stage.Available;

        bool isNormal = Settings.DiscordMusicState == DiscordMusicState.Normal && music;
        _normalTransition.Target = isNormal ? 1 : 0;

        // В верхнем пузыре музыка видна только когда она НЕ в нормальном (нижнем) режиме!
        bool bubbleHasMusic = music && !isNormal;
        bool split = compact && (bubbleHasMusic || record || timer || shelf || update);

        double targetMusicWidth = Settings.DiscordMusicMini ^ _bubbleDropArmed ? MusicMiniWidth : MusicBubbleWidth;
        if (_bubbleDropToNormal) targetMusicWidth = 210;

        _musicBubbleWidth.Target = targetMusicWidth;
        _bubbleSplit.Target = split ? 1 : 0;
        if (compact) _bubbleSplit.Tune(140, 17);
        else _bubbleSplit.Tune(300, 30);
        if (!split && !isNormal) return;

        _bubbleMusic.Target = bubbleHasMusic ? 1 : 0;
        _bubbleRecord.Target = record ? 1 : 0;
        _bubbleTimer.Target = timer ? 1 : 0;
        _bubbleShelf.Target = shelf ? 1 : 0;
        _bubbleUpdate.Target = update ? 1 : 0;
        if (shelf) _shelfBubbleWidth.Target = MeasureShelfBubble();
        if (_bubbleSplit.Value >= BarelyVisible) return;

        _musicBubbleWidth.Snap(_musicBubbleWidth.Target);
        _bubbleMusic.Snap(_bubbleMusic.Target);
        _bubbleRecord.Snap(_bubbleRecord.Target);
        _bubbleTimer.Snap(_bubbleTimer.Target);
        _bubbleShelf.Snap(_bubbleShelf.Target);
        _bubbleUpdate.Snap(_bubbleUpdate.Target);
        _shelfBubbleWidth.Snap(_shelfBubbleWidth.Target);
        _normalTransition.Snap(_normalTransition.Target);
    }

    static double RubberBand(double distance, double limit) => limit * (1 - 1 / (distance * DragFollow / limit + 1));

    void TuneDragSprings(bool tight)
    {
        (double stiffness, double damping) = tight ? (900.0, 60.0) : (300.0, 21.0);
        _width.Tune(stiffness, damping);
        _height.Tune(stiffness, damping);
        foreach (Spring spring in _spotSprings) spring.Tune(stiffness, damping);
        _radius.Tune(tight ? 900 : 300, tight ? 60 : 30);
        _moveX.Tune(tight ? 900 : 300, tight ? 60 : 30);
        _bubbleDragY.Tune(tight ? 900 : 300, tight ? 60 : 30);
    }

    /// <summary>
    /// Как далеко остров может уехать от центра экрана налево и направо, чтобы пилюля и пузырь справа от неё остались на экране.
    /// </summary>
    (double Left, double Right) MoveRange(double pillWidth, double bubbleSplit)
    {
        double size = Math.Max(_userScale.Value, MinScale);
        double reach = SystemParameters.PrimaryScreenWidth / 2 - ScreenEdgeMargin - pillWidth * size / 2;
        double bubble = (BubbleGap + Bubble.Width) * size * Math.Clamp(bubbleSplit, 0, 1);
        return (Math.Max(reach, 0), Math.Max(reach - bubble, 0));
    }

    void PlaceWindow(double pillWidth)
    {
        (double left, double right) = MoveRange(pillWidth, _bubbleSplit.Value);
        double at = Math.Round(_baseLeft + Math.Clamp(_moveX.Value, -left, right));
        if (at == Left) return;

        Left = at;
        if (_glass.IsOn) _glass.Place();
    }

    double MeasureShelfBubble()
    {
        BubbleShelf.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(ShelfBubbleWidth, BubbleShelf.DesiredSize.Width - BubbleShelf.Margin.Right + 2 * ShelfBubblePadding);
    }

    void StartShapeLoop() => _shapeLoop.Start();

    bool AdvanceShape(double dt)
    {
        bool moving = false;
        foreach (Spring spring in _springs) moving |= spring.Advance(dt);
        ApplyShape();
        if (!moving)
        {
            EndMorph();
            EndGrowFromBubble();
        }
        return moving || IsLyricSettling;
    }

    void ApplyShape()
    {
        double w = Math.Max(_width.Value, MinPillSize), h = Math.Max(_height.Value, MinPillSize);
        double r = Math.Clamp(_radius.Value, 0, Math.Min(w, h) / 2);
        var pill = new Rect((HostWidth - w) / 2, 0, w, h);
        double scale = Math.Max(_scale.Value, MinScale), size = Math.Max(_userScale.Value, MinScale);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;

        _pillRect = pill;
        IslandShift.X = _islandShift.Value;
        Pill.Width = w;
        Pill.Height = h;
        Pill.CornerRadius = new CornerRadius(r);
        Shadow.Opacity = Math.Clamp((h - CompactMaxHeight) / ShadowFadeHeight, 0, 1);
        CompactGlow.Width = w;
        CompactGlow.Height = Math.Min(h, 34);
        CompactGlow.Margin = new Thickness(0, h - CompactGlow.Height, 0, 0);
        if (pill != _clipRect || r != _clipRadius)
        {
            _clipRect = pill;
            _clipRadius = r;
            Host.Clip = Shared.Clip = Shadow.Data = Squircle.Of(pill, r);
            Shadow.Clip = _glass.IsOn ? Around(Shadow.Data) : null;
        }

        if (_view == View.Menu && (_menuTimerPresetsOpen || MenuTimerPresetClip.Height > 0))
            ApplyMenuTimerPresets(Math.Clamp(h - PillShapes[View.Menu].Height, 0, MenuTimerPresets.Height));
        FitMediaViews(w, h);
        if (_morphing) FitViews(w, h);
        ApplyMotionBlur();
        ApplyShelfScroll();
        ApplyLookScroll();
        ApplyUpdateNotesScroll();
        StretchVolumeBar();

        IslandScale.ScaleX = IslandScale.ScaleY = scale;
        RootSize.ScaleX = RootSize.ScaleY = size;
        RootMove.Y = _offsetY.Value + _topGap.Value / size;
        PlaceWindow(w);
        SharpenLyricCache(dpi);

        PlaceCoverAndBars(pill);
        PlaceBubble(pill, r, scale);
        CutGlass(dpi);
    }

    void SharpenLyricCache(double dpi)
    {
        double sharp = Math.Max(_userScale.Target, MinScale) * dpi;
        if (Math.Abs(_lyricCache.RenderAtScale - sharp) > CacheScaleTolerance) _lyricCache.RenderAtScale = sharp;
        AimLyricAtWholePixels(dpi);
    }

    bool IsLyricSettling => _lyricSnapX.Value != _lyricSnapX.Target || _lyricSnapY.Value != _lyricSnapY.Target;

    void AimLyricAtWholePixels(double dpi)
    {
        if (_view == View.Media && LyricBox.IsVisible && Root.TransformToAncestor(this) is Transform place)
        {
            Point inIsland = LyricBox.TranslatePoint(new Point(-LyricSnap.X, -LyricSnap.Y), Island);
            Matrix toWindow = place.Value;
            Point unscaled = toWindow.Transform(inIsland + VisualTreeHelper.GetOffset(Island));
            double pixelsPerUnit = Math.Max(toWindow.M11, MinScale) * dpi;
            _lyricSnapX.Target = (Math.Round(unscaled.X * dpi) - unscaled.X * dpi) / pixelsPerUnit;
            _lyricSnapY.Target = (Math.Round(unscaled.Y * dpi) - unscaled.Y * dpi) / pixelsPerUnit;
        }
        LyricSnap.X = _lyricSnapX.Value;
        LyricSnap.Y = _lyricSnapY.Value;
    }

    static Geometry Around(Geometry hole)
    {
        var around = new GeometryGroup { FillRule = FillRule.EvenOdd };
        around.Children.Add(new RectangleGeometry(new Rect(-ShadowReach, -ShadowReach, HostWidth + 2 * ShadowReach, HostHeight + 2 * ShadowReach)));
        around.Children.Add(hole);
        return around;
    }

    void CutGlass(double dpi)
    {
        if (!_glass.IsOn || Body.TransformToAncestor(this) is not Transform place) return;
        Matrix toPixels = place.Value;
        toPixels.Scale(dpi, dpi);
        _glass.Cut(Body.PillInside, Body.BubbleInside, toPixels);
    }

    void SyncGlass(bool animate)
    {
        if (Settings.Glass)
        {
            _glass.Show();
            Reshape();
        }
        Body.Thin(Settings.Glass, animate ? GlassTime : TimeSpan.Zero, HideGlassOnceCovered);
    }

    void HideGlassOnceCovered()
    {
        if (Settings.Glass || !_glass.IsOn) return;
        _glass.Hide();
        Reshape();
    }

    void Reshape()
    {
        _clipRect = Rect.Empty;
        ApplyShape();
    }

    void ApplyMotionBlur()
    {
        double speed = Math.Sqrt(_width.Velocity * _width.Velocity + _height.Velocity * _height.Velocity);
        double smear = _morphing && _grab == Grab.None ? Math.Min(speed / MotionBlurSpeed, MaxMotionBlur) : 0;
        _motionBlur.Radius = smear;
        Host.Effect = smear > MinMotionBlur ? _motionBlur : null;
    }

    void PlaceBubble(Rect pill, double r, double scale)
    {
        double split = _bubbleSplit.Value, bubble = Math.Max(_bubbleScale.Value, MinScale);
        double music = Math.Max(_bubbleMusic.Value, 0), record = Math.Max(_bubbleRecord.Value, 0);
        double timer = Math.Max(_bubbleTimer.Value, 0), shelf = Math.Max(_bubbleShelf.Value, 0);
        double update = Math.Max(_bubbleUpdate.Value, 0);

        double musicWidth = _musicBubbleWidth.Value;
        double[] weights = [music, record, timer, shelf, update];
        double[] widths = [musicWidth, RecordBubbleWidth, TimerBubbleWidth, _shelfBubbleWidth.Value, UpdateBubbleWidth];
        double total = 0, seams = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            total += widths[i] * weights[i];
            for (int j = i + 1; j < weights.Length; j++)
            {
                double between = 1;
                for (int k = i + 1; k < j; k++) between *= 1 - Math.Clamp(weights[k], 0, 1);
                seams += weights[i] * weights[j] * between;
            }
        }
        double wide = Math.Max(total - BubbleOverlap * seams, Bubble.Height);
        bool apart = split > BubbleVisibleSplit && (music > 0 || record > 0 || timer > 0 || shelf > 0 || update > 0);
        Bubble.SetVisible(apart);
        Bubble.Width = wide;
        BubbleBody.Clip = Squircle.Of(new Rect(0, 0, wide, Bubble.Height), Bubble.Height / 2);
        BubbleMusic.Opacity = Math.Clamp(music * 2 - 1, 0, 1);
        BubbleMusic.Width = musicWidth;
        BubbleGlow.Width = wide;
        BubbleGlow.Height = Bubble.Height;

        BubbleMusicInfo.SetVisible(false);
        double open = Math.Clamp((musicWidth - MusicMiniWidth) / (MusicBubbleWidth - MusicMiniWidth), 0, 1);
        BubbleCoverBox.Margin = new Thickness(6 + 2 * open, 0, 0, 0);
        BubbleEq.Opacity = Math.Clamp(open * 2 - 1, 0, 1);

        BubbleRecord.Opacity = Math.Clamp(record * 2 - 1, 0, 1);
        BubbleTimer.Opacity = Math.Clamp(timer * 2 - 1, 0, 1);
        double afterMusic = (musicWidth - BubbleOverlap) * music;
        BubbleRecord.Margin = new Thickness(afterMusic, 0, 0, 0);
        BubbleTimer.Margin = new Thickness(afterMusic + (RecordBubbleWidth - BubbleOverlap) * record, 0, 0, 0);
        BubbleShelf.Opacity = Math.Clamp(shelf * 2 - 1, 0, 1);
        BubbleShelf.Margin = new Thickness(0, 0, ShelfBubblePadding + (ShelfBubblePaddingWithTimer - ShelfBubblePadding) * Math.Clamp(Math.Max(timer, record), 0, 1), 0);
        BubbleUpdate.Opacity = Math.Clamp(update * 2 - 1, 0, 1);
        BubbleUpdate.SetVisible(update > 0.05);
        double afterItems = afterMusic + (RecordBubbleWidth - BubbleOverlap) * record + (TimerBubbleWidth - BubbleOverlap) * timer + (_shelfBubbleWidth.Value - BubbleOverlap) * shelf;
        BubbleUpdate.Margin = new Thickness(afterItems, 0, 0, 0);

        BubbleScale.ScaleX = BubbleScale.ScaleY = bubble;
        BubbleBody.Opacity = _growing ? Math.Clamp(1 - _bubbleGrow.Value * 4, 0, 1) : Math.Clamp(split * 4 - 3, 0, 1);
        Bubble.IsHitTestVisible = split > BubbleClickableSplit && !_growing;

        // Верхний пузырь ВСЕГДА позиционируется сбоку от основного острова
        double tuck = Math.Max(r - Bubble.Height / 2, 0) * BubbleTuckFactor * (1 - Math.Clamp(split, 0, 1)) * scale;
        BubbleMove.X = _growing ? _growBubbleX : (pill.Width * scale - wide) / 2 + (BubbleGap + wide) * split - tuck + (Settings.DiscordMusicState == DiscordMusicState.Normal ? 0 : _bubbleDrag.Value);
        BubbleMove.Y = Settings.DiscordMusicState == DiscordMusicState.Normal ? 0 : _bubbleDragY.Value;
        double past = ((BubbleGap + wide) * split - wide - tuck + (Settings.DiscordMusicState == DiscordMusicState.Normal ? 0 : _bubbleDrag.Value)) / scale;
        Rect shape = apart ? new Rect(pill.Right + past, (Settings.DiscordMusicState == DiscordMusicState.Normal ? 0 : _bubbleDragY.Value) / scale, wide * bubble / scale, Bubble.Height * bubble / scale) : Rect.Empty;

        if (_growing && _bottomMusicRect.IsEmpty) shape = SwallowedPill(pill, scale);
        else _bubbleRect = shape;

        // Нижний остров музыки (нормальный режим под Дискордом)
        double normProg = Math.Clamp(_normalTransition.Value, 0, 1);
        Rect bottomShape = Rect.Empty;
        if (normProg > 0.01 && WantsMusicBubble())
        {
            BottomMusic.SetVisible(true);
            double bottomScale = Math.Max(_bottomMusicScale.Value, MinScale);
            BottomMusicScale.ScaleX = BottomMusicScale.ScaleY = bottomScale;
            double normWidth = 210;
            BottomMusic.Width = normWidth;
            BottomMusicBody.Clip = Squircle.Of(new Rect(0, 0, normWidth, 34), 17);
            BottomMusicGlow.Width = normWidth;
            BottomMusicGlow.Height = 34;

            double sideMusicX = (pill.Width * scale - normWidth) / 2 + (BubbleGap + wide) * split - tuck;
            double targetBottomX = _growing ? _growBubbleX : (pill.Width * scale - normWidth) / 2 + _bubbleDrag.Value;
            double targetBottomY = (pill.Bottom + BubbleGap) * normProg + _bubbleDragY.Value;

            BottomMusicMove.X = sideMusicX + (targetBottomX - sideMusicX) * normProg;
            BottomMusicMove.Y = targetBottomY;
            BottomMusic.Opacity = Math.Clamp(normProg * 2 - 0.5, 0, 1);
            BottomMusic.IsHitTestVisible = normProg > 0.7 && !_growing;

            string title = _media.HasTrack ? _media.Name : "";
            if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(TitleBig.Text)) title = TitleBig.Text;
            NormalMusicTitle.Text = title;
            string artist = string.IsNullOrWhiteSpace(_media.Artist) ? UnknownArtist : _media.Artist;
            NormalMusicArtist.Text = _sourceName.Length > 0 ? _sourceName + " · " + artist : artist;

            double bottomShapeX = (HostWidth - normWidth) / 2 + _bubbleDrag.Value / scale;
            double bottomShapeY = (pill.Bottom + BubbleGap) * normProg + _bubbleDragY.Value / scale;
            bottomShape = new Rect(bottomShapeX, bottomShapeY, normWidth * bottomScale / scale, 34 * bottomScale / scale);
        }
        else
        {
            BottomMusic.SetVisible(false);
        }
        _bottomMusicRect = bottomShape;

        // Goo отрисовывает подложки и ободки для пилюли, верхнего пузыря и нижнего острова музыки
        Body.SetShape(pill, r, shape, bottomShape);

        // Единая виртуальная система координат волны (Aura) на все острова и части
        double minLeft = pill.Left;
        double maxRight = pill.Right;
        if (!shape.IsEmpty)
        {
            minLeft = Math.Min(minLeft, shape.Left);
            maxRight = Math.Max(maxRight, shape.Right);
        }
        if (!bottomShape.IsEmpty)
        {
            minLeft = Math.Min(minLeft, bottomShape.Left);
            maxRight = Math.Max(maxRight, bottomShape.Right);
        }
        double clusterWidth = Math.Max(maxRight - minLeft, pill.Width);

        CompactGlow.VirtualWidth = clusterWidth;
        CompactGlow.VirtualOffset = pill.Left - minLeft;

        BubbleGlow.VirtualWidth = clusterWidth;
        BubbleGlow.VirtualOffset = (!shape.IsEmpty ? shape.Left : 0) - minLeft;

        BottomMusicGlow.VirtualWidth = clusterWidth;
        BottomMusicGlow.VirtualOffset = (!bottomShape.IsEmpty ? bottomShape.Left : 0) - minLeft;
    }

    /// <summary>
    /// Нажали на пузырь, и остров открывается из него: растущая форма стартует на месте пузыря и едет к центру,
    /// а прежняя пилюля втягивается в неё, как капля. Вызывать после того, как вид уже выбран.
    /// </summary>
    void BeginGrowFromBubble()
    {
        if (_bubbleRect.IsEmpty || _pillRect.IsEmpty || ShapeOf(_view).Height < CompactMaxHeight) return;

        double scale = Math.Max(_scale.Value, MinScale);
        Rect bubble = !_bottomMusicRect.IsEmpty && _normalTransition.Value > 0.5 ? _bottomMusicRect : _bubbleRect;
        _growFrom = _pillRect;
        _growBubbleX = !_bottomMusicRect.IsEmpty && _normalTransition.Value > 0.5 ? BottomMusicMove.X : BubbleMove.X;
        _growing = true;

        // остров начинает как пузырь: тот же размер и то же место, а прежний размер пилюли остаётся в «капле»
        _width.Snap(bubble.Width);
        _height.Snap(Math.Max(bubble.Height, MinPillSize));
        _radius.Snap(bubble.Height / 2);
        _islandShift.Snap(((bubble.X + bubble.Width / 2) - (_growFrom.X + _growFrom.Width / 2)) * scale);
        _islandShift.Target = 0;
        _bubbleGrow.Snap(0);
        _bubbleGrow.Target = 1;
        StartShapeLoop();
    }

    void EndGrowFromBubble()
    {
        if (!_growing) return;
        _growing = false;
        _bubbleGrow.Snap(0);
        _islandShift.Snap(0);
    }

    /// <summary>Прежняя пилюля стоит на месте, пока остров сдвинут, и стягивается к растущей форме.</summary>
    Rect SwallowedPill(Rect pill, double scale)
    {
        double e = Math.Clamp(_bubbleGrow.Value, 0, 1), shift = _islandShift.Value / scale;
        var old = new Rect(_growFrom.X - shift, _growFrom.Y, _growFrom.Width, _growFrom.Height);
        return new Rect(
            old.X + (pill.X - old.X) * e, old.Y + (pill.Y - old.Y) * e,
            old.Width + (pill.Width - old.Width) * e, old.Height + (pill.Height - old.Height) * e);
    }

    void FitViews(double w, double h)
    {
        foreach (FrameworkElement view in _views.Values)
        {
            if (view.Visibility != Visibility.Visible) continue;
            FitView(view, Math.Clamp(Math.Min(w / view.Width, h / view.Height), MinViewScale, MaxViewScale), (h - view.Height) / 2);
        }
    }

    static void FitView(FrameworkElement view, double scale, double offsetY)
    {
        ScaleTransform fit = FitScaleOf(view);
        fit.ScaleX = fit.ScaleY = scale;
        OffsetOf(view).Y = offsetY;
    }

    void EndMorph()
    {
        if (!_morphing || _grab == Grab.Pull) return;
        _morphing = false;
        EndGrowFromBubble();
        foreach (FrameworkElement view in _views.Values) FitView(view, 1, 0);
        Host.Effect = null;
    }
}
