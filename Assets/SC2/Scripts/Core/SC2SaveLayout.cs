using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>Where one saved map lives on disk, and in what order its files are tried on load. Pure path and list logic (no Unity
/// types, no file access), so it is covered by tests.
///
/// A map is saved as a single self-contained folder, "&lt;Map&gt;.sc2mapshare", inside the save directory:
/// <code>
/// &lt;Map&gt;.sc2mapshare/
///     &lt;Map&gt;.sc2terrain.json                 optional legacy copy of the whole map (see SC2SaveSettings.WriteLegacyJson)
///     &lt;Map&gt;.sc2mapdoc/manifest.json          the document: everything except the large binary data
///     Blobs/&lt;hash[0:2]&gt;/&lt;hash&gt;.bin          the large binary data, content-addressed
/// </code>
/// That folder is the one source of truth. The same files directly in the save directory (the layout of earlier versions, with one
/// shared Blobs pool) are only read, when a map has no folder of its own, and are never written.</summary>
public static class SC2SaveLayout
{
    public const string LegacyJsonSuffix = ".sc2terrain.json";
    public const string ShareSuffix = ".sc2mapshare";
    public const string DocumentSuffix = ".sc2mapdoc";
    public const string ManifestFileName = "manifest.json";
    public const string BlobsFolderName = "Blobs";
    public const string BackupsFolderName = "Backups";

    /// <summary>The map name as a file name: characters a file name cannot hold become '_', an empty name becomes "Untitled".</summary>
    public static string SafeName(string mapName)
    {
        var name = string.IsNullOrWhiteSpace(mapName) ? "Untitled" : mapName;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    // ---- The map's own folder (the source of truth) ----
    public static string ShareDirectory(string saveDirectory, string mapName) => Path.Combine(saveDirectory, SafeName(mapName) + ShareSuffix);
    public static string ShareJsonPath(string saveDirectory, string mapName) => Path.Combine(ShareDirectory(saveDirectory, mapName), SafeName(mapName) + LegacyJsonSuffix);
    public static string ShareDocumentDirectory(string saveDirectory, string mapName) => Path.Combine(ShareDirectory(saveDirectory, mapName), SafeName(mapName) + DocumentSuffix);
    public static string ShareManifestPath(string saveDirectory, string mapName) => Path.Combine(ShareDocumentDirectory(saveDirectory, mapName), ManifestFileName);
    public static string ShareBlobsDirectory(string saveDirectory, string mapName) => Path.Combine(ShareDirectory(saveDirectory, mapName), BlobsFolderName);

    // ---- Earlier layout, directly in the save directory (read only) ----
    public static string LegacyJsonPath(string saveDirectory, string mapName) => Path.Combine(saveDirectory, SafeName(mapName) + LegacyJsonSuffix);
    public static string LegacyDocumentDirectory(string saveDirectory, string mapName) => Path.Combine(saveDirectory, SafeName(mapName) + DocumentSuffix);
    public static string LegacyManifestPath(string saveDirectory, string mapName) => Path.Combine(LegacyDocumentDirectory(saveDirectory, mapName), ManifestFileName);
    public static string LegacyBlobsDirectory(string saveDirectory) => Path.Combine(saveDirectory, BlobsFolderName);

    // ---- Backups: one self-contained folder per snapshot, laid out like the map's own folder ----
    public static string BackupsDirectory(string saveDirectory, string mapName) => Path.Combine(saveDirectory, BackupsFolderName, SafeName(mapName));

    /// <summary>The map name a folder or file name stands for, or null when the name does not end in <paramref name="suffix"/>.</summary>
    public static string StemOf(string fileOrFolderName, string suffix)
    {
        if (string.IsNullOrEmpty(fileOrFolderName) || string.IsNullOrEmpty(suffix)) return null;
        if (fileOrFolderName.Length <= suffix.Length) return null;
        return fileOrFolderName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileOrFolderName.Substring(0, fileOrFolderName.Length - suffix.Length)
            : null;
    }

    /// <summary>The names of the saved maps: every map with a folder of its own, then the maps that only exist in the earlier layout,
    /// each name once (compared without regard to case), sorted.</summary>
    public static List<string> MergeMapNames(IEnumerable<string> shareStems, IEnumerable<string> legacyStems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var stems in new[] { shareStems, legacyStems })
        {
            if (stems == null) continue;
            foreach (var stem in stems)
                if (!string.IsNullOrEmpty(stem) && seen.Add(stem))
                    result.Add(stem);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public enum Source
    {
        ShareManifest,
        ShareJson,
        LegacyManifest,
        LegacyJson,
    }

    /// <summary>The order the files of a map are tried on load: the map's own folder first (its manifest, then its JSON), the earlier
    /// layout only after that. A source whose file does not exist is left out.</summary>
    public static List<Source> LoadOrder(bool shareManifest, bool shareJson, bool legacyManifest, bool legacyJson)
    {
        var order = new List<Source>(4);
        if (shareManifest) order.Add(Source.ShareManifest);
        if (shareJson) order.Add(Source.ShareJson);
        if (legacyManifest) order.Add(Source.LegacyManifest);
        if (legacyJson) order.Add(Source.LegacyJson);
        return order;
    }
}
