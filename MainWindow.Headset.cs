using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double HeadsetSeconds = 3.6;
    const string LowChargeLabel = "Низкий заряд";
    static readonly Color HeadsetYellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
    static readonly SolidColorBrush HeadsetDark = new(Color.FromArgb(0xD0, 0, 0, 0));
    static readonly string[] EarbudNames = ["pods", "buds"];
    static readonly TimeSpan HeadsetGadgetDelay = TimeSpan.FromMilliseconds(80);
    static readonly TimeSpan HeadsetFillDelay = TimeSpan.FromMilliseconds(240);
    static readonly TimeSpan HeadsetFillRun = TimeSpan.FromMilliseconds(900);
    static readonly TimeSpan HeadsetTickStagger = TimeSpan.FromMilliseconds(55);

    static Gadget.Model HeadsetModel(AudioService.Output device) =>
        !device.Headphones ? Gadget.Model.Speaker
        : EarbudNames.Any(part => device.Name.Contains(part, StringComparison.OrdinalIgnoreCase)) ? Gadget.Model.Earbuds
        : Gadget.Model.Headphones;

    void ShowHeadset(string name, Gadget.Model model, int level, bool low)
    {
        if (_ringing) return;

        Color tint = low ? _red.Color : HeadsetYellow;
        var solid = new SolidColorBrush(tint);
        HeadsetGlow.Tint(tint);
        HeadsetGlow.Pulse = low;
        HeadsetPercent.Text = level + "%";
        HeadsetLabel.Text = low ? LowChargeLabel : name;

        ShowTransient(View.Headset, HeadsetSeconds);
        HeadsetView.UpdateLayout();
        double reach = level / 100.0;
        Brush Over(UIElement part, Brush aside) => !low && HeadsetGlow.Lights(part, reach) ? HeadsetDark : aside;
        HeadsetBattery.Fill = Over(HeadsetBattery, low ? Brushes.White : solid);
        HeadsetPercent.Foreground = Over(HeadsetPercent, low ? Brushes.White : solid);
        HeadsetLabel.Foreground = low ? Brushes.White : HeadsetGlow.Across(HeadsetLabel, reach, HeadsetDark.Color, Colors.White);
        foreach (Shape tick in HeadsetTicks.Children.OfType<Shape>()) tick.Fill = Over(tick, solid);

        HeadsetGlow.BeginAnimation(ChargeGlow.ReachProperty, Delayed(0, reach, HeadsetFillDelay, HeadsetFillRun,
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        HeadsetGadget.Play(model, low, HeadsetGadgetDelay, TimeSpan.FromSeconds(HeadsetSeconds));

        TimeSpan wait = HeadsetFillDelay;
        foreach (UIElement tick in HeadsetTicks.Children)
        {
            double shown = (double)tick.GetAnimationBaseValue(OpacityProperty);
            tick.BeginAnimation(OpacityProperty, Delayed(0, shown, wait, TimeSpan.FromMilliseconds(220)));
            wait += HeadsetTickStagger;
        }
    }
}
