using System.Text.Json;

namespace SC2InfoBdfEditorApp;

/// <summary>
/// Tiny persisted settings file (window bounds + last opened .bdf path) —
/// written next to the exe so no registry/AppData indirection is needed
/// for a single-user desktop tool. Best-effort throughout: a missing or
/// corrupt settings file just means default startup behavior (centered
/// window, no file opened), never a fatal error.
/// </summary>
internal static class AppSettings
{
    private static readonly string SettingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");

    public sealed class Data
    {
        // int.MinValue means "no saved position — center the window
        // instead" (0,0 is itself a valid real screen position, so it
        // can't double as the sentinel).
        public int WindowX { get; set; } = int.MinValue;
        public int WindowY { get; set; } = int.MinValue;
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public string LastOpenedFilePath { get; set; } = "";
    }

    public static Data Load()
    {
        try
        {
            if (!System.IO.File.Exists(SettingsPath)) return new Data();
            var json = System.IO.File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<Data>(json) ?? new Data();
        }
        catch
        {
            return new Data();
        }
    }

    public static void Save(Data data)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            System.IO.File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, options));
        }
        catch
        {
            // Best-effort only — a failed settings write must never crash
            // the app on close.
        }
    }
}
