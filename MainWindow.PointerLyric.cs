using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

public partial class MainWindow
{
    PointerLyric? _pointerLyric;
    bool _pointerLyricBroken;

    void PointerLyric_Click(object sender, RoutedEventArgs e)
    {
        Settings.PointerLyric = !Settings.PointerLyric;
        UpdateSwitches(true);
        UpdatePointerLyric();
    }

    void UpdatePointerLyric()
    {
        string text = PointerLyricText();
        if ((text.Length == 0 && _pointerLyric == null) || _pointerLyricBroken) return;

        if (_pointerLyric == null)
        {
            try
            {
                _pointerLyric = new PointerLyric((FontFamily)FindResource("Display"), () => _hovered);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                _pointerLyricBroken = true;
                return;
            }
            StayOnTop();
        }
        _pointerLyric.ShowLine(text);
    }

    void StayOnTop()
    {
        Native.KeepOnTop(_hwnd);
        _glass.Place();
        if (_pointerLyric != null) Native.PutBehind(_pointerLyric.Handle, _glass.IsOn ? _glass.Handle : _hwnd);
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
