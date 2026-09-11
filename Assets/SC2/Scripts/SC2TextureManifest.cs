using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reverse lookup for texture Resources paths, built from a manifest file
/// SC2TerrainBdfImporter.RegenerateTextureManifest() generates (one
/// Resources-relative path per line, extension stripped the same way
/// Resources.Load itself strips it). Exists because neither
/// Resources.LoadAll nor Resources.FindObjectsOfTypeAll expose the path a
/// loaded Texture2D actually came from — Unity gives no runtime API to list
/// Resources folder contents at all, so without a manifest there is no way
/// to reconstruct which of several same-named textures under different
/// subfolders a given object reference is.
///
/// This matters because this project's real texture library has thousands
/// of leaf-name collisions: numbered diffuse/normal/specular sets like
/// "1_d"/"2_d" repeat across many different material subfolders by the
/// source game's own naming convention, and a sample check across the live
/// project confirmed those collisions are genuinely different content, not
/// harmless duplicate copies — a bare-name search picks whichever one
/// happens to load first, silently wrong.
/// </summary>
public static class SC2TextureManifest
{
    private static Dictionary<string, List<string>> _pathsByLeafName;

    private static void EnsureLoaded()
    {
        if (_pathsByLeafName != null)
            return;

        _pathsByLeafName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var manifest = Resources.Load<TextAsset>("SC2TextureManifest");
        if (manifest == null)
            return;

        foreach (var rawLine in manifest.text.Split('\n'))
        {
            var path = rawLine.Trim();
            if (path.Length == 0)
                continue;

            var slash = path.LastIndexOf('/');
            var leaf = slash >= 0 ? path[(slash + 1)..] : path;
            if (!_pathsByLeafName.TryGetValue(leaf, out var list))
                _pathsByLeafName[leaf] = list = new List<string>();
            list.Add(path);
        }
    }

    /// <summary>Every known Resources path (manifest-relative, matching
    /// Resources.Load's expected format) whose leaf matches this bare
    /// Texture2D.name — empty if no manifest exists or nothing matches.</summary>
    public static IReadOnlyList<string> GetCandidatePaths(string leafName)
    {
        if (string.IsNullOrEmpty(leafName))
            return Array.Empty<string>();

        EnsureLoaded();
        return _pathsByLeafName.TryGetValue(leafName, out var list) ? list : Array.Empty<string>();
    }
}
