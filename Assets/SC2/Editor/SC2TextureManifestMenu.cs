using UnityEditor;
using UnityEngine;

/// <summary>
/// Manual entry point for SC2TerrainBdfImporter.RegenerateTextureManifest() —
/// that call already runs automatically after every terrain.bdf import copies
/// new textures in, but this project's Resources/SC2Textures folder was
/// populated by many imports done before the manifest existed at all, so a
/// one-time backfill over everything already on disk is needed too.
/// </summary>
public static class SC2TextureManifestMenu
{
    [MenuItem("SC2/Regenerate Texture Manifest")]
    public static void Regenerate()
    {
        SC2TerrainBdfImporter.RegenerateTextureManifest();
        SC2TextureCatalogBuilder.Build(false);
        Debug.Log("[SC2TextureManifestMenu] Texture manifest regenerated.");
    }
}
