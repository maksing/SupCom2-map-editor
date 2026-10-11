using System.Collections.Generic;
using System.IO;
using BdfIO.Core;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;

/// <summary>
/// Fills <see cref="SC2TerrainDocument.MapshareTextures"/> when a map is saved: the textures that live only on this machine, kept in the
/// map's own share so the map has them wherever it is opened (see <see cref="SC2MapshareTextures"/> for how a loader falls back to them).
/// Covers textures imported from a file (the "file:" paths of materials) and the textures an imported terrain.bdf names that the game
/// archive does not have.
/// </summary>
public static class SC2MapshareTextureCapture
{
    // A terrain.bdf is parsed once per distinct content: the texture paths it names, by blob hash.
    private static readonly Dictionary<string, List<string>> BdfTexturePaths = new Dictionary<string, List<string>>();

    public static void Capture(SC2MapDocument document, ISC2BlobStore store)
    {
        var terrain = document?.Terrain;
        if (terrain == null || store == null) return;

        var list = new List<SC2MapshareTextureDocument>();
        foreach (var material in terrain.Materials)
        {
            CaptureFile(list, store, material.Diffuse?.ResourcePath);
            CaptureFile(list, store, material.Normal?.ResourcePath);
            CaptureFile(list, store, material.Mask?.ResourcePath);
        }

        foreach (var entry in terrain.ImportedEntries)
        {
            foreach (var overrides in entry.MaterialOverrides)
                foreach (var path in overrides.TextureResourcePaths.Values)
                    CaptureFile(list, store, path);

            foreach (var path in TexturePathsOf(entry, store))
            {
                // A texture the game ships, or one in a configured game install, is found there; only the rest has to travel with the map.
                if (SC2VanillaArchiveClassifier.IsVanillaGamePath(SC2TerrainMaterialFactory.NormalizePath(path))) continue;
                byte[] bytes = null;
                var extension = ".dds";
                if (SC2TerrainMaterialFactory.TryReadLooseTextureBytes(path, out var found, out var foundExtension))
                {
                    bytes = found;
                    extension = foundExtension;
                }
                SC2MapshareTextures.Capture(list, store, path, bytes, extension);
            }
        }

        terrain.MapshareTextures = list;
    }

    private static void CaptureFile(List<SC2MapshareTextureDocument> list, ISC2BlobStore store, string resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath) || !resourcePath.StartsWith(SC2MapshareTextures.FilePrefix, System.StringComparison.Ordinal)) return;
        var file = resourcePath.Substring(SC2MapshareTextures.FilePrefix.Length);
        byte[] bytes = null;
        try { if (File.Exists(file)) bytes = File.ReadAllBytes(file); }
        catch (IOException) { }
        SC2MapshareTextures.Capture(list, store, resourcePath, bytes, Path.GetExtension(file));
    }

    private static IEnumerable<string> TexturePathsOf(SC2ImportedTerrainEntryDocument entry, ISC2BlobStore store)
    {
        if (entry.OriginalBdf.IsEmpty || !store.Contains(entry.OriginalBdf.ContentHash)) return new string[0];
        if (BdfTexturePaths.TryGetValue(entry.OriginalBdf.ContentHash, out var cached)) return cached;
        var paths = new List<string>();
        try
        {
            using var stream = store.OpenRead(entry.OriginalBdf.ContentHash);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var model = TerrainBdf.Parse(BdfContainer.Read(memory.ToArray(), TerrainBdf.Padding));
            paths.AddRange(SC2TerrainBdfImporter.CollectTexturePaths(model));
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[SC2MapshareTextureCapture] could not read the textures of imported terrain '{entry.DisplayName}': {ex.Message}");
        }
        BdfTexturePaths[entry.OriginalBdf.ContentHash] = paths;
        return paths;
    }
}
