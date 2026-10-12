using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland;

public partial class MainWindow
{
    const double WeatherPagePadding = 14;
    const double HourIconSize = 20;
    const string LocationSettings = "ms-settings:privacy-location";
    const string Minus = "−";

    readonly WeatherService _weather = new();
    readonly List<(TextBlock At, Icon Sky, TextBlock Degrees)> _weatherHours = [];

    void PrepareWeatherHours()
    {
        for (int i = 0; i < WeatherService.ShownHours; i++)
        {
            var at = new TextBlock { FontSize = 11, Foreground = _dim, HorizontalAlignment = HorizontalAlignment.Center };
            var sky = new Icon { Width = HourIconSize, Height = HourIconSize, Margin = new Thickness(0, 6, 0, 5) };
            var degrees = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            WeatherHours.Children.Add(new StackPanel { Children = { at, sky, degrees } });
            _weatherHours.Add((at, sky, degrees));
        }
    }

    void SyncWeather()
    {
        WeatherService.Report? now = _weather.Now;
        InfoWeatherRow.SetVisible(now != null);
        WeatherNow.SetVisible(now != null);
        WeatherHours.SetVisible(now != null);
        WeatherNote.SetVisible(now == null);
        MenuWeather.Text = now != null ? DescribeDegrees(now.Degrees) : "";
        if (now != null) ShowWeather(now);
        else ShowWeatherNote();
        FitWeatherPage();
    }

    void ShowWeather(WeatherService.Report now)
    {
        Glyph glyph = GlyphOf(now.Sky, now.Night);
        InfoWeatherIcon.Kind = MenuWeatherIcon.Kind = WeatherIcon.Kind = glyph;
        WeatherIcon.Fill = glyph == Glyph.Sun ? _orange : glyph == Glyph.Moon ? _indigo : Brushes.White;
        InfoWeather.Text = WeatherDegrees.Text = DescribeDegrees(now.Degrees);
        WeatherSky.Text = now.Sky switch
        {
            Sky.Clear => "Ясно",
            Sky.Cloudy => "Облачно",
            Sky.Overcast => "Пасмурно",
            Sky.Fog => "Туман",
            Sky.Rain => "Дождь",
            Sky.Snow => "Снег",
            _ => "Гроза",
        };
        WeatherFeels.Text = "Ощущается как " + DescribeDegrees(now.Feels);
        WeatherWind.Text = $"Ветер {now.Wind} м/с";

        for (int i = 0; i < _weatherHours.Count; i++)
        {
            (TextBlock at, Icon sky, TextBlock degrees) = _weatherHours[i];
            bool known = i < now.Hours.Length;
            ((UIElement)at.Parent).Visibility = known ? Visibility.Visible : Visibility.Hidden;
            if (!known) continue;

            WeatherService.Hour hour = now.Hours[i];
            at.Text = hour.At.ToString("00") + ":00";
            sky.Kind = GlyphOf(hour.Sky, hour.Night);
            degrees.Text = DescribeDegrees(hour.Degrees);
        }
    }

    void ShowWeatherNote()
    {
        (string title, string text, string action) = _weather.State switch
        {
            WeatherService.Stage.Off => ("Погода выключена", "Остров не узнаёт, где вы, и ничего не запрашивает", "Включить"),
            WeatherService.Stage.NoPlace => ("Нет доступа к месту", "Включите в Windows расположение и доступ к нему для классических приложений", "Открыть параметры"),
            WeatherService.Stage.Failed => ("Не получилось", "Остров попробует ещё раз сам", "Попробовать сейчас"),
            _ => ("Узнаю погоду…", "", ""),
        };
        WeatherNoteTitle.Text = title;
        WeatherNoteText.Text = text;
        WeatherNoteText.SetVisible(text.Length > 0);
        WeatherAction.Content = action;
        WeatherAction.SetVisible(action.Length > 0);
    }

    void FitWeatherPage()
    {
        WeatherBody.Measure(new Size(WeatherView.Width, double.PositiveInfinity));
        double height = Math.Ceiling(WeatherBody.DesiredSize.Height) + WeatherPagePadding;
        if (height == WeatherView.Height) return;

        WeatherView.Height = height;
        if (_view == View.Weather) UpdateTargets();
    }

    static string DescribeDegrees(int degrees) => (degrees > 0 ? "+" : degrees < 0 ? Minus : "") + Math.Abs(degrees) + "°";

    static Glyph GlyphOf(Sky sky, bool night) => sky switch
    {
        Sky.Clear => night ? Glyph.Moon : Glyph.Sun,
        Sky.Cloudy => night ? Glyph.Cloud : Glyph.CloudSun,
        Sky.Overcast => Glyph.Cloud,
        Sky.Fog => Glyph.Fog,
        Sky.Rain => Glyph.Rain,
        Sky.Snow => Glyph.Snow,
        _ => Glyph.Storm,
    };

    void WeatherRow_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(Panel.Weather);
        _ = _weather.RefreshAsync(_weather.State != WeatherService.Stage.Ready);
    }

    void WeatherAction_Click(object sender, RoutedEventArgs e)
    {
        if (_weather.State == WeatherService.Stage.Off) SetWeather(true);
        else if (_weather.State == WeatherService.Stage.NoPlace) OpenLocationSettings();
        _ = _weather.RefreshAsync(true);
    }

    void Weather_Click(object sender, RoutedEventArgs e)
    {
        SetWeather(!Settings.Weather);
        _ = _weather.RefreshAsync(true);
    }

    void SetWeather(bool on)
    {
        Settings.Weather = on;
        UpdateSwitches(true);
    }

    static void OpenLocationSettings()
    {
        try { Process.Start(new ProcessStartInfo(LocationSettings) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { App.Log(ex); }
    }
}
