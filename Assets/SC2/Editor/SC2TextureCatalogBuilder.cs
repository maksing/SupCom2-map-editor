using System;
using System.Collections.Generic;
using System.IO;
using SC2.MapEditor.Textures;
using UnityEditor;
using UnityEngine;

public static class SC2TextureCatalogBuilder
{
    private const string TextureRoot = "Assets/SC2/Resources/SC2Textures";
    private const string CatalogPath = "Assets/SC2/Resources/SC2TextureCatalog.json";
    private const string ThumbnailFolder = "Assets/SC2/Resources/SC2TextureThumbnails";
    private const int TileSize = 128;
    private const int AtlasSize = 1024;
    private const int TilesPerAxis = AtlasSize / TileSize;
    private const int TilesPerPage = TilesPerAxis * TilesPerAxis;

    [MenuItem("SC2/Textures/Rebuild Catalog")]
    public static void RebuildCatalog() => Build(false);

    [MenuItem("SC2/Textures/Rebuild Catalog + Thumbnails")]
    public static void RebuildCatalogAndThumbnails() => Build(true);

    public static void Build(bool thumbnails)
    {
        var flatGuids = AssetDatabase.FindAssets("t:Texture2D", new[] { TextureRoot });
        // Unity's native DDS importer resolves a cubemap/volume DDS to a
        // Cubemap/Texture3D asset, not Texture2D — invisible to the scan
        // above, so both need their own pass.
        var cubeGuids = AssetDatabase.FindAssets("t:Cubemap", new[] { TextureRoot });
        var volumeGuids = AssetDatabase.FindAssets("t:Texture3D", new[] { TextureRoot });
        Array.Sort(flatGuids, StringComparer.Ordinal);
        Array.Sort(cubeGuids, StringComparer.Ordinal);
        Array.Sort(volumeGuids, StringComparer.Ordinal);
        var data = new SC2TextureCatalogData { Version = 2 };
        Texture2D atlas = null;
        RenderTexture tileTarget = null;
        var currentPage = -1;
        var index = 0;
        var totalCount = flatGuids.Length + cubeGuids.Length + volumeGuids.Length;
        try
        {
            if (thumbnails)
            {
                Directory.CreateDirectory(ThumbnailFolder);
                tileTarget = RenderTexture.GetTemporary(TileSize, TileSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            }

            void AddRawEntry(string assetPath, SC2TextureCatalogEntry entry, Texture2D previewSource)
            {
                if (thumbnails && previewSource != null)
                {
                    var page = index / TilesPerPage;
                    if (page != currentPage)
                    {
                        if (atlas != null) SaveAtlas(atlas, currentPage);
                        if (atlas != null) UnityEngine.Object.DestroyImmediate(atlas);
                        atlas = NewAtlas();
                        currentPage = page;
                    }
                    var slot = index % TilesPerPage;
                    var x = slot % TilesPerAxis;
                    var y = slot / TilesPerAxis;
                    Graphics.Blit(previewSource, tileTarget);
                    var old = RenderTexture.active;
                    RenderTexture.active = tileTarget;
                    atlas.ReadPixels(new Rect(0, 0, TileSize, TileSize), x * TileSize, y * TileSize, false);
                    RenderTexture.active = old;
                    entry.ThumbnailResourcePath = "SC2TextureThumbnails/page_" + page.ToString("D4");
                    entry.ThumbnailPage = page;
                    entry.ThumbnailX = x / (float)TilesPerAxis;
                    entry.ThumbnailY = y / (float)TilesPerAxis;
                    entry.ThumbnailWidth = 1f / TilesPerAxis;
                    entry.ThumbnailHeight = 1f / TilesPerAxis;
                }
                data.Entries.Add(entry);
                if ((index & 63) == 0) EditorUtility.DisplayProgressBar("SC2 Texture Catalog", assetPath, index / (float)Math.Max(1, totalCount));
                index++;
            }

            void AddEntry(string assetPath, string resourcePath, Texture2D previewSource, int width, int height,
                int mipCount, bool hasAlpha, string encoding, bool isCubemap, bool isVolume)
            {
                var entry = SC2TextureCatalog.CreateEntry(resourcePath, SC2TextureSource.Vanilla);
                entry.Width = width;
                entry.Height = height;
                entry.MipCount = mipCount;
                entry.HasAlpha = hasAlpha;
                entry.Encoding = encoding;
                entry.IsCubemap = isCubemap;
                entry.IsVolume = isVolume;
                AddRawEntry(assetPath, entry, previewSource);
            }

            var recognizedAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var guid in flatGuids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                recognizedAssetPaths.Add(assetPath);
                var resourcePath = ToResourcePath(assetPath);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (texture == null || string.IsNullOrEmpty(resourcePath)) continue;
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                var hasAlpha = importer != null && importer.DoesSourceTextureHaveAlpha();
                var encoding = Path.GetExtension(assetPath).TrimStart('.').ToUpperInvariant();
                AddEntry(assetPath, resourcePath, texture, texture.width, texture.height, texture.mipmapCount, hasAlpha, encoding, false, false);
            }

            foreach (var guid in cubeGuids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                recognizedAssetPaths.Add(assetPath);
                var resourcePath = ToResourcePath(assetPath);
                if (string.IsNullOrEmpty(resourcePath)) continue;
                var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(assetPath);
                if (cubemap == null) continue;
                // No Read/Write Enabled requirement here — BuildCubemapPreview
                // extracts each face via Graphics.CopyTexture (GPU-to-GPU,
                // same reason the flat-texture thumbnail path above never
                // needed CPU readability either), not Cubemap.GetPixels.
                // GetPixels was tried first and confirmed unreliable: forcing
                // TextureImporter.isReadable + SaveAndReimport did not
                // actually make real vanilla cubemap DDS assets CPU-readable
                // in this project.
                var preview = thumbnails ? BuildCubemapPreview(cubemap) : null;
                var encoding = Path.GetExtension(assetPath).TrimStart('.').ToUpperInvariant();
                AddEntry(assetPath, resourcePath, preview, cubemap.width, cubemap.height, cubemap.mipmapCount, true, encoding, true, false);
                if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
            }

            foreach (var guid in volumeGuids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                recognizedAssetPaths.Add(assetPath);
                var resourcePath = ToResourcePath(assetPath);
                if (string.IsNullOrEmpty(resourcePath)) continue;
                var volume = AssetDatabase.LoadAssetAtPath<Texture3D>(assetPath);
                if (volume == null) continue;
                // No Read/Write Enabled requirement here either (same reason
                // as the cubemap loop above) — BuildVolumeMiddleSlicePreview
                // samples via a shader, not Texture3D.GetPixels.
                var preview = thumbnails ? BuildVolumeMiddleSlicePreview(volume) : null;
                var encoding = Path.GetExtension(assetPath).TrimStart('.').ToUpperInvariant();
                AddEntry(assetPath, resourcePath, preview, volume.width, volume.height, volume.mipmapCount, true, encoding, false, true);
                if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
            }

            // Fallback pass: real .dds files Unity's own native importer
            // flatly refuses to import as ANY asset type — confirmed for
            // cubemap/volume shapes (a real vanilla ColorCorrectionCube
            // file logs "Unsupported ... file" from Unity's own
            // IHVImageFormats.cpp) and, separately, for flat 2D files with a
            // format/shape Unity's native importer rejects (DXT3, non-4-
            // aligned dimensions, luminance/alpha-only) even though this
            // project's own decoder (SC2CustomTextureImporter) handles them
            // correctly. Any of these never appears in any
            // AssetDatabase.FindAssets scan above no matter what type is
            // searched for, since Unity created no asset at all. Read every
            // real .dds file directly off disk and parse it with this
            // project's own DDS decoder instead of relying on Unity's
            // importer at all — skipping anything the 3 passes above
            // already catalogued. Every shape TryLoadBytesAny can decode
            // (flat/cube/volume alike) is cataloged here — there is no
            // reason to discard a successfully-decoded flat result, unlike
            // an earlier version of this pass.
            var dataPath = Application.dataPath.Replace('\\', '/');
            var textureRootFullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dataPath), TextureRoot)).Replace('\\', '/');
            if (Directory.Exists(textureRootFullPath))
            {
                foreach (var fullPath in Directory.GetFiles(textureRootFullPath, "*.dds", SearchOption.AllDirectories))
                {
                    var normalizedFullPath = fullPath.Replace('\\', '/');
                    var assetPath = "Assets" + normalizedFullPath.Substring(dataPath.Length);
                    if (recognizedAssetPaths.Contains(assetPath)) continue;
                    var resourcePath = ToResourcePath(assetPath);
                    if (string.IsNullOrEmpty(resourcePath)) continue;

                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(normalizedFullPath); }
                    catch (Exception ex) { Debug.LogWarning($"[SC2TextureCatalog] Could not read '{assetPath}': {ex.Message}"); continue; }

                    if (!SC2CustomTextureImporter.TryLoadBytesAny(bytes, ".dds", out var texture, out var preview, out var isCubemap, out var isVolume, out var loadError))
                    {
                        if (!string.IsNullOrEmpty(loadError)) Debug.LogWarning($"[SC2TextureCatalog] '{assetPath}': {loadError}");
                        continue;
                    }
                    var entry = SC2TextureCatalog.CreateEntry(resourcePath, SC2TextureSource.Vanilla);
                    // No real Resources-loadable asset exists for this file
                    // (that's exactly why it's here) — point GamePath at the
                    // real disk path instead, so SC2TextureBrowser's
                    // ResolveRealAsset (which already prefers ResourcePath,
                    // falling back to GamePath via
                    // SC2CustomTextureImporter.TryLoadAny) resolves it
                    // correctly when picked.
                    entry.ResourcePath = "";
                    entry.GamePath = normalizedFullPath;
                    entry.Width = texture.width;
                    entry.Height = texture.height;
                    entry.MipCount = texture.mipmapCount;
                    entry.HasAlpha = true;
                    entry.Encoding = "DDS";
                    entry.IsCubemap = isCubemap;
                    entry.IsVolume = isVolume;
                    AddRawEntry(assetPath, entry, thumbnails ? preview : null);

                    UnityEngine.Object.DestroyImmediate(texture);
                    if (preview != null && !ReferenceEquals(preview, texture)) UnityEngine.Object.DestroyImmediate(preview);
                }
            }

            if (atlas != null) SaveAtlas(atlas, currentPage);
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
            File.WriteAllText(CatalogPath, JsonUtility.ToJson(data, true));
            AssetDatabase.Refresh();
            ConfigureGeneratedImporters();
            Debug.Log($"[SC2TextureCatalog] Generated {data.Entries.Count} metadata entries" + (thumbnails ? " with thumbnail atlases." : "."));
        }
        finally
        {
            if (atlas != null) UnityEngine.Object.DestroyImmediate(atlas);
            if (tileTarget != null) RenderTexture.ReleaseTemporary(tileTarget);
            EditorUtility.ClearProgressBar();
        }
    }

    private static Material _cubemapFaceMaterial;

    /// <summary>Both Graphics.CopyTexture and Graphics.ConvertTexture turned
    /// out to reject a Cubemap->Texture2D pair outright ("unsupported source
    /// and destination texture types", confirmed against a real vanilla
    /// cubemap during an actual Player build) — neither texture-copy API
    /// supports crossing dimensions, only same-dimension transfers. Real
    /// GPU sampling via a small dedicated shader (Resources/
    /// SC2CubemapFacePreview.shader, matching this project's existing
    /// SC2TextureChannelPreview.shader pattern for the same class of
    /// preview-only need) is the only remaining GPU-side option — samples
    /// samplerCUBE at a per-face reconstructed direction, needing no CPU
    /// Read/Write access on the source asset at all.</summary>
    private static Material GetCubemapFaceMaterial()
    {
        if (_cubemapFaceMaterial != null) return _cubemapFaceMaterial;
        var shader = Resources.Load<Shader>("SC2CubemapFacePreview") ?? Shader.Find("Hidden/SC2/CubemapFacePreview");
        if (shader == null) return null;
        _cubemapFaceMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        return _cubemapFaceMaterial;
    }

    private static Texture2D BuildCubemapPreview(Cubemap cubemap)
    {
        var material = GetCubemapFaceMaterial();
        if (material == null) return null;
        // Graphics.Blit's `source` argument only ever sets _MainTex, which
        // this shader doesn't use — the cubemap must be bound to _Cube
        // explicitly, or the shader samples an unbound (blank) texture.
        material.SetTexture("_Cube", cubemap);
        var faces = new Texture2D[6];
        var target = RenderTexture.GetTemporary(cubemap.width, cubemap.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        try
        {
            for (var i = 0; i < 6; i++)
            {
                material.SetFloat("_Face", i);
                Graphics.Blit(cubemap, target, material);
                RenderTexture.active = target;
                var tex = new Texture2D(cubemap.width, cubemap.height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
                tex.ReadPixels(new Rect(0, 0, cubemap.width, cubemap.height), 0, 0, false);
                tex.Apply(false, false);
                faces[i] = tex;
            }
            return SC2CustomTextureImporter.ComposeCubeCrossPreview(faces);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            foreach (var f in faces) if (f != null) UnityEngine.Object.DestroyImmediate(f);
        }
    }

    private static Material _volumeSliceMaterial;

    /// <summary>Same reasoning as GetCubemapFaceMaterial/BuildCubemapPreview
    /// above — Texture3D.GetPixels needs CPU Read/Write access this
    /// project's vanilla-imported volume assets don't reliably grant, so
    /// this samples the middle Z-slice via a dedicated shader (Resources/
    /// SC2Texture3DSlicePreview.shader) instead.</summary>
    private static Material GetVolumeSliceMaterial()
    {
        if (_volumeSliceMaterial != null) return _volumeSliceMaterial;
        var shader = Resources.Load<Shader>("SC2Texture3DSlicePreview") ?? Shader.Find("Hidden/SC2/Texture3DSlicePreview");
        if (shader == null) return null;
        _volumeSliceMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        return _volumeSliceMaterial;
    }

    private static Texture2D BuildVolumeMiddleSlicePreview(Texture3D volume)
    {
        var material = GetVolumeSliceMaterial();
        if (material == null) return null;
        material.SetTexture("_Volume", volume);
        material.SetFloat("_Slice", 0.5f);
        var target = RenderTexture.GetTemporary(volume.width, volume.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(volume, target, material);
            RenderTexture.active = target;
            var tex = new Texture2D(volume.width, volume.height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            tex.ReadPixels(new Rect(0, 0, volume.width, volume.height), 0, 0, false);
            tex.Apply(false, false);
            return tex;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
    }

    private static Texture2D NewAtlas()
    {
        var atlas = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false, false);
        var clear = new Color32[AtlasSize * AtlasSize];
        atlas.SetPixels32(clear);
        return atlas;
    }

    private static void SaveAtlas(Texture2D atlas, int page)
    {
        atlas.Apply(false, false);
        File.WriteAllBytes(Path.Combine(ThumbnailFolder, "page_" + page.ToString("D4") + ".png"), atlas.EncodeToPNG());
    }

    private static string ToResourcePath(string assetPath)
    {
        const string marker = "/Resources/";
        var index = assetPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "";
        var path = assetPath.Substring(index + marker.Length).Replace('\\', '/');
        return path.Substring(0, path.Length - Path.GetExtension(path).Length);
    }

    private static void ConfigureGeneratedImporters()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { ThumbnailFolder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.mipmapEnabled = false;
            importer.isReadable = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }
    }
}

public sealed class SC2TextureCatalogBuildProcessor : UnityEditor.Build.IPreprocessBuildWithReport
{
    public int callbackOrder => -1000;
    public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
    {
        SC2TextureCatalogBuilder.Build(true);
    }
}
