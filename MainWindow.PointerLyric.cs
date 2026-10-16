using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

public partial class MainWindow
{
    PointerLyric? _pointerLyric;

    void PointerLyric_Click(object sender, RoutedEventArgs e)
    {
        Settings.PointerLyric = !Settings.PointerLyric;
        UpdateSwitches(true);
        UpdatePointerLyric();
    }

    void UpdatePointerLyric()
    {
        string text = PointerLyricText();
        if (text.Length == 0 && _pointerLyric == null) return;

        _pointerLyric ??= new PointerLyric((FontFamily)FindResource("Display"));
        _pointerLyric.ShowLine(text);
    }

    string PointerLyricText()
    {
        if (!Settings.PointerLyric || !Settings.Lyrics || !_media.IsPlaying || _hiddenByFullscreen) return "";

        LyricsService.Line[] lines = _lyrics.LinesFor(_media.Duration);
        int index = LineAt(lines, LyricTime);
        string text = index < 0 ? "" : lines[index].Text.Trim();
        return Lyric.IsWordless(text) ? "" : text;
    }
}
