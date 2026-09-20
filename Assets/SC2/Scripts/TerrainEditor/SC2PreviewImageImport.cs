using System.IO;
using UnityEngine;

/// <summary>Imports a picture from disk as the map's minimap or map-select preview (_PC_mapshot): the file is
/// decoded, fitted to the exact target size (cover and crop, see SC2ImageResample) and stored in the
/// form the export uses.</summary>
public static class SC2PreviewImageImport
{
    /// <summary>Asks for an image file (png, jpg, dds). Null when cancelled.</summary>
    public static string PickImageFile(string title)
    {
#if UNITY_EDITOR
        return UnityEditor.EditorUtility.OpenFilePanel(title, "", "png,jpg,jpeg,dds");
#else
        return SC2RuntimeFileDialog.OpenFile(title, "Image (*.png;*.jpg;*.jpeg;*.dds)", "png");
#endif
    }

    /// <summary>Decodes an image file into RGBA32 rows, bottom row first (Unity's own orientation, the picture
    /// shows upright when drawn).</summary>
    public static bool TryLoadRgba(string path, out byte[] rgba, out int width, out int height, out string error)
    {
        rgba = null; width = height = 0; error = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            Texture2D source;
            var ownsSource = true;
            if (string.Equals(Path.GetExtension(path), ".dds", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!SC2CustomTextureImporter.TryLoadBytesAny(bytes, ".dds", out var texture, out var preview, out _, out _, out error))
                    return false;
                source = preview != null ? preview : texture as Texture2D;
                if (source == null) { error = "The DDS file is not a flat 2D texture."; return false; }
                ownsSource = false;
            }
            else
            {
                source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(source, bytes, false) || source.width <= 0 || source.height <= 0)
                {
                    Object.DestroyImmediate(source);
                    error = "Could not decode the file as an image.";
                    return false;
                }
            }
            width = source.width; height = source.height;
            rgba = ReadRgba(source);
            if (ownsSource) Object.DestroyImmediate(source);
            return true;
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // Any texture format (including compressed ones) to plain RGBA32, through a render texture.
    private static byte[] ReadRgba(Texture2D source)
    {
        var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply(false, false);
            var data = readable.GetRawTextureData();
            var copy = new byte[data.Length];
            System.Array.Copy(data, copy, data.Length);
            Object.DestroyImmediate(readable);
            return copy;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    /// <summary>The map-select preview from a picture file: fitted to 448x206, stored as opaque BGRA in the
    /// natural row order SC2TerrainData.MapShotBgraPixels uses (the vanilla frame is stamped at export and
    /// on the thumbnail).</summary>
    public static bool TryImportMapShot(string path, out byte[] bgra, out string error)
    {
        bgra = null;
        if (!TryLoadRgba(path, out var rgba, out var w, out var h, out error)) return false;
        var fitted = SC2ImageResample.CoverResizeRgba(rgba, w, h, SC2MapPreviewCapture.MapShotWidth, SC2MapPreviewCapture.MapShotHeight);
        bgra = new byte[fitted.Length];
        for (var i = 0; i < fitted.Length; i += 4)
        {
            bgra[i] = fitted[i + 2]; bgra[i + 1] = fitted[i + 1]; bgra[i + 2] = fitted[i]; bgra[i + 3] = 255;
        }
        return true;
    }

    /// <summary>The minimap from a picture file: fitted to 1024x1024 and stored as PNG bytes.</summary>
    public static bool TryImportMinimapPng(string path, out byte[] png, out string error)
    {
        png = null;
        if (!TryLoadRgba(path, out var rgba, out var w, out var h, out error)) return false;
        var size = SC2MapPreviewCapture.MinimapSize;
        var fitted = SC2ImageResample.CoverResizeRgba(rgba, w, h, size, size);
        for (var i = 3; i < fitted.Length; i += 4) fitted[i] = 255;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.LoadRawTextureData(fitted);
        tex.Apply(false, false);
        png = ImageConversion.EncodeToPNG(tex);
        Object.DestroyImmediate(tex);
        return png != null && png.Length > 0;
    }
}
