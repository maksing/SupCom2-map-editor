using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Locates the real vanilla map corpus (gamedata.scd/maps, already unpacked
/// per-map into subfolders). Tests here degrade to Skip (not Fail) when the
/// corpus isn't present on the machine running them, since it's the user's
/// own legally-owned game data and is never checked into source control.
/// </summary>
public static class TestCorpus
{
    // Known location on the machine this was developed on. Override via the
    // SC2_VANILLA_MAPS_DIR environment variable on any other machine.
    private static readonly string[] CandidatePaths =
    {
        System.Environment.GetEnvironmentVariable("SC2_VANILLA_MAPS_DIR"),
        @"C:\Users\user\Desktop\Новая папка (9)\gamedata\gamedata.scd\maps",
    };

    public static string MapsRoot { get; } = CandidatePaths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));

    public static bool IsAvailable => MapsRoot != null;

    public static IEnumerable<string> MapNames =>
        IsAvailable
            // gamedata.scd/maps/ contains at least one non-map folder ("_reports",
            // presumably left over from earlier diagnostics tooling) alongside
            // the real per-map folders — filter to folders that actually look
            // like a map (contain their own *.terrain.win.bdf) rather than
            // assuming every subdirectory is a map.
            ? Directory.EnumerateDirectories(MapsRoot)
                .Select(Path.GetFileName)
                .Where(n => File.Exists(Path.Combine(MapsRoot, n, $"{n}.terrain.win.bdf")))
                .OrderBy(n => n)
            : Enumerable.Empty<string>();

    public static string MapDir(string mapName) => Path.Combine(MapsRoot, mapName);

    public static string SkipReason => IsAvailable
        ? null
        : "Vanilla map corpus not found on this machine (set SC2_VANILLA_MAPS_DIR or place it at the developer's known path). " +
          "This is expected/OK — these tests characterize real vanilla data and are skipped, not failed, when that data isn't present.";
}
