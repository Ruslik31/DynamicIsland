using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicIsland;

/// <summary>Плагин BetterDiscord вшит в exe: остров сам кладёт его в папку плагинов и обновляет.</summary>
static partial class PluginInstaller
{
    const string ResourceName = "DiscordIslandBridge.plugin.js";
    const string FileName = "DiscordIslandBridge.plugin.js";

    public enum Stage { NoBetterDiscord, Missing, Outdated, Installed }

    static string PluginsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterDiscord", "plugins");

    static string TargetPath => Path.Combine(PluginsFolder, FileName);

    public static string BundledVersion => VersionOf(ReadBundled()) ?? "?";

    public static Stage Check()
    {
        if (!Directory.Exists(PluginsFolder)) return Stage.NoBetterDiscord;
        if (!File.Exists(TargetPath)) return Stage.Missing;
        try
        {
            return string.Equals(Normalize(File.ReadAllText(TargetPath, Encoding.UTF8)), Normalize(ReadBundled()), StringComparison.Ordinal) ? Stage.Installed : Stage.Outdated;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return Stage.Outdated;
        }
    }

    /// <summary>Кладёт плагин в BetterDiscord. BetterDiscord подхватывает файл сам, включить плагин нужно в его настройках.</summary>
    public static bool Install()
    {
        if (!Directory.Exists(PluginsFolder)) return false;
        try
        {
            File.WriteAllText(TargetPath, ReadBundled(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    static string ReadBundled()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static string? VersionOf(string source) => VersionPattern().Match(source) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"@version\s+([0-9.]+)")]
    private static partial Regex VersionPattern();
}
