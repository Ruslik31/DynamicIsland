using Microsoft.Win32;

namespace DynamicIsland;

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "DiscordIsland";

    public static string? RegisteredPath
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(Name) is string value)
                {
                    return value.Trim('\"', ' ');
                }
            }
            catch { }
            return null;
        }
    }

    public static bool Enabled
    {
        get
        {
            string? registered = RegisteredPath;
            string? current = Environment.ProcessPath;
            return registered != null && current != null
                && string.Equals(registered, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(Name, $"\"{exe}\"");
        else key.DeleteValue(Name, false);
    }

    public static void UpdateRegisteredPath(string newPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(Name, $"\"{newPath}\"");
    }
}
