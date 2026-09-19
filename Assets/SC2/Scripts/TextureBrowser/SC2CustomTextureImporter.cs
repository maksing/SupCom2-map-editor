using System;
using System.IO;
using System.Security.Cryptography;
using SC2.MapEditor.Document;
using SC2.MapEditor.Textures;
using UnityEngine;

public sealed class SC2CustomTextureImportResult
{
    public SC2TextureCatalogEntry Entry;
    // Widened from Texture2D: a cubemap/volume DDS resolves to a real
    // Cubemap/Texture3D here (see SC2CustomTextureImporter.TryLoadBytesAny) —
    // all three are IS-A Texture, and every Unity API this result actually
    // flows through (GUI.DrawTexture, Material.SetTexture,
    // SC2TerrainMaterialFactory's resource-path dictionary) already accepts
    // Texture, not Texture2D specifically.
    public Texture Texture;
    public string SourceHash = "";
    public string GameDdsHash = "";
    public string Error = "";
    public bool Success => Entry != null && Texture != null && string.IsNullOrEmpty(Error);
}

[Serializable]
public sealed class SC2CustomTextureMetadata
{
    public int Version = 1;
    public string SourceHash = "";
    public string ContentHash = "";
    public SC2TextureUsage Usage;
    public int Width;
    public int Height;
    public int MipCount;
    public bool HasAlpha;
    public string Encoding = "";
}

public static class SC2CustomTextureImporter
{
    public static string LibraryDirectory => Path.Combine(Application.persistentDataPath, "TextureLibrary");

    /// <summary>Deletes an imported texture's files from the library: the
    /// converted .dds, its thumbnail (.thumb.png) and metadata
    /// (.sc2tex.json). Only ever touches files inside LibraryDirectory —
    /// anything outside it is skipped, so a mangled catalog entry can't
    /// delete an arbitrary file. Removing the catalog entry itself is the
    /// caller's job (SC2TextureCatalog.Remove).</summary>
    public static bool TryDelete(SC2TextureCatalogEntry entry, out string error)
    {
        error = null;
        if (entry == null || entry.Source != SC2TextureSource.Custom || string.IsNullOrEmpty(entry.GamePath))
        {
            error = "Not an imported texture.";
            return false;
        }
        try
        {
            var library = Path.GetFullPath(LibraryDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var path in new[] { entry.GamePath, entry.ThumbnailFilePath, entry.GamePath + ".sc2tex.json" })
            {
                if (string.IsNullOrEmpty(path)) continue;
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(library, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(full)) File.Delete(full);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static SC2CustomTextureImportResult Import(string sourcePath, SC2TextureUsage usage, bool overwrite)
    {
        var result = new SC2CustomTextureImportResult();
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("Texture source was not found.", sourcePath);

            var sourceBytes = File.ReadAllBytes(sourcePath);
            result.SourceHash = Hash(sourceBytes);
            Directory.CreateDirectory(LibraryDirectory);
            var safeName = Sanitize(Path.GetFileNameWithoutExtension(sourcePath));
            var destination = Path.Combine(LibraryDirectory, safeName + "_" + result.SourceHash.Substring(0, 10) + ".dds");

            Texture texture; Texture2D preview; bool isCubemap; bool isVolume; string loadError;
            if (File.Exists(destination) && !overwrite)
            {
                if (!TryLoadBytesAny(File.ReadAllBytes(destination), ".dds", out texture, out preview, out isCubemap, out isVolume, out loadError))
                { result.Error = loadError; return result; }
            }
            else
            {
                if (!TryLoadBytesAny(sourceBytes, Path.GetExtension(sourcePath), out texture, out preview, out isCubemap, out isVolume, out loadError))
                    throw new InvalidDataException(loadError);
                var ddsBytes = IsDds(sourceBytes) ? sourceBytes : EncodeMaterialDds((Texture2D)texture);
                result.GameDdsHash = Hash(ddsBytes);
                WriteAtomic(destination, ddsBytes);
            }
            result.Texture = texture;

            if (string.IsNullOrEmpty(result.GameDdsHash)) result.GameDdsHash = Hash(File.ReadAllBytes(destination));
            result.Texture.name = safeName;
            var entry = SC2TextureCatalog.CreateEntry(destination, SC2TextureSource.Custom);
            entry.Name = safeName;
            entry.Usage = usage;
            entry.IsCubemap = isCubemap;
            entry.IsVolume = isVolume;
            entry.Width = result.Texture.width;
            entry.Height = result.Texture.height;
            entry.MipCount = result.Texture.mipmapCount;
            entry.HasAlpha = HasUsefulAlpha(result.Texture);
            entry.Encoding = ReadEncoding(destination);
            entry.SourceHash = result.SourceHash;
            entry.ContentHash = result.GameDdsHash;
            entry.GamePath = destination;
            entry.ThumbnailFilePath = destination + ".thumb.png";
            // preview is always a flat Texture2D — the real drawable
            // representation regardless of whether the resolved asset above
            // is a Texture2D/Cubemap/Texture3D (see TryLoadBytesAny).
            WriteThumbnail(preview != null ? preview : result.Texture as Texture2D, entry.ThumbnailFilePath);
            if (preview != null && !ReferenceEquals(preview, result.Texture)) UnityEngine.Object.Destroy(preview);
            WriteMetadata(destination, entry);
            entry.SearchKey = SC2TextureCatalogEntry.BuildSearchKey(entry.Name, entry.ResourcePath, entry.GamePath, entry.Category, entry.Source, entry.Usage);
            result.Entry = entry;
            SC2TextureCatalog.Instance.AddOrReplace(entry);
            SC2TerrainMaterialFactory.RegisterResourcePath(result.Texture, "file:" + destination.Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            if (result.Texture != null) UnityEngine.Object.Destroy(result.Texture);
            result.Texture = null;
        }
        return result;
    }

    /// <summary>Always resolves to a flat, drawable Texture2D — a
    /// Cubemap/Texture3D source resolves to its preview representation
    /// here, never the real object (existing callers, e.g.
    /// SC2TextureThumbnailCache, only ever need something they can draw
    /// directly). Use TryLoadAny for the real resolved asset.
    /// `linear`: true for normal maps / RNM basis-blend masks — raw
    /// vector/blend data, not color, must not be gamma-decoded when
    /// sampled. Defaults to false (gamma/sRGB, the correct interpretation
    /// for every other real caller of this method) so every existing call
    /// site keeps its current behavior unchanged.</summary>
    public static bool TryLoad(string path, out Texture2D texture, out string error, bool linear = false, bool disableMipmaps = false)
    {
        texture = null;
        error = null;
        try
        {
            if (!TryLoadBytesAny(File.ReadAllBytes(path), Path.GetExtension(path), out var any, out var preview, out _, out _, out error, linear, disableMipmaps))
                return false;
            texture = preview;
            if (!ReferenceEquals(any, preview) && any != null) UnityEngine.Object.Destroy(any);
            return texture != null;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>The real resolved asset — a Cubemap/Texture3D for a
    /// cubemap/volume DDS, a Texture2D otherwise. Used where the caller
    /// actually needs to hand the real object on (e.g. SC2TextureBrowser's
    /// Accept()), not just draw a preview.</summary>
    /// <summary>`linear`: same meaning as TryLoad's own — true for normal
    /// maps/RNM masks, raw vector/blend data that must not be gamma-decoded.
    /// Was missing entirely (always decoded sRGB regardless of the real
    /// usage) — every OTHER loader in this file (TryLoad/TryLoadFromArchive/
    /// TryLoadPreviewFromArchive) already threads this through, but this is
    /// the one SC2TextureBrowser.ResolveRealAsset itself calls to build the
    /// REAL Texture2D handed to a material's Diffuse/Normal/Mask field — so
    /// a picked Normal/RNM texture's own GPU-level sRGB tag was baked wrong
    /// at the moment it was picked, before either preview path (browser
    /// thumbnail vs. left-panel inline preview) ever got a chance to differ:
    /// no amount of correctly setting a PREVIEW blit's read/write mode can
    /// fix a source texture whose own texture object is already tagged
    /// sRGB. Confirmed real: a vanilla normal map's true colors are
    /// purple/lavender (matches its own browser thumbnail, decoded via the
    /// correctly-linear TryLoad/TryLoadFromArchive path), but the picked
    /// material's own texture — resolved through this function — rendered
    /// visibly different (a flatter, more saturated blue) once assigned.</summary>
    public static bool TryLoadAny(string path, out Texture texture, out string error, bool linear = false)
    {
        texture = null;
        error = null;
        try
        {
            if (!TryLoadBytesAny(File.ReadAllBytes(path), Path.GetExtension(path), out texture, out var preview, out _, out _, out error, linear))
                return false;
            if (!ReferenceEquals(preview, texture) && preview != null) UnityEngine.Object.Destroy(preview);
            return texture != null;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>The real resolved asset, sourced directly from a .scd
    /// archive entry instead of a loose file — reader.OpenEntry(entryPath)'s
    /// bytes feed straight into the same TryLoadBytesAny every other loader
    /// here already uses, so this is the one new primitive the archive-
    /// first texture pipeline (terrain-material resolution, the vanilla
    /// catalog build, "Import from game archive...") routes through
    /// instead of each reinventing archive-byte-to-Texture decoding.
    /// Extension is inferred from entryPath's own suffix since an archive
    /// entry has no separate file-extension source to ask for. `linear`:
    /// same meaning as TryLoad's own — true for normal maps/RNM masks, raw
    /// vector/blend data that must not be gamma-decoded.</summary>
    public static bool TryLoadFromArchive(ISC2SourceContainerReader reader, string entryPath, out Texture texture, out string error, bool linear = false, bool disableMipmaps = false)
    {
        texture = null;
        error = null;
        try
        {
            var bytes = ReadArchiveEntryBytes(reader, entryPath);
            if (!TryLoadBytesAny(bytes, Path.GetExtension(entryPath), out texture, out var preview, out _, out _, out error, linear, disableMipmaps))
                return false;
            if (!ReferenceEquals(preview, texture) && preview != null) UnityEngine.Object.Destroy(preview);
            return texture != null;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>Always resolves to a flat, drawable Texture2D — same
    /// contract as TryLoad, but sourced from a .scd archive entry instead
    /// of a loose file. Used by SC2TextureThumbnailCache for archive-
    /// sourced catalog entries, which (like every other entry) only ever
    /// need a flat preview for browsing/thumbnails, never the real
    /// Cubemap/Texture3D object. `linear`: same meaning as TryLoad's own —
    /// true for normal maps/RNM masks, raw vector/blend data that must not
    /// be gamma-decoded.</summary>
    public static bool TryLoadPreviewFromArchive(ISC2SourceContainerReader reader, string entryPath, out Texture2D texture, out string error, bool linear = false)
    {
        texture = null;
        error = null;
        try
        {
            var bytes = ReadArchiveEntryBytes(reader, entryPath);
            if (!TryLoadBytesAny(bytes, Path.GetExtension(entryPath), out var any, out var preview, out _, out _, out error, linear))
                return false;
            texture = preview;
            if (!ReferenceEquals(any, preview) && any != null) UnityEngine.Object.Destroy(any);
            return texture != null;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static byte[] ReadArchiveEntryBytes(ISC2SourceContainerReader reader, string entryPath)
    {
        using var stream = reader.OpenEntry(entryPath);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Cheap check — reads only the 128-byte DDS header (not the
    /// full file) to determine whether a .dds file's width/height are both
    /// powers of two. Used by SC2TerrainMaterialFactory to decide whether a
    /// texture needs this project's own decoder at all: Unity's native DDS
    /// import is confirmed correct for POT files and for every non-DDS
    /// format, and is only confirmed broken (deep mip levels below mip 0
    /// decoded as pure block noise) for a specific class of real vanilla
    /// NPOT files (dirt_01_d, sand_01_d). Returns false for anything that
    /// isn't a readable DDS file with a valid width/height, so callers
    /// default to leaving Unity's native import alone.</summary>
    public static bool IsNpotDds(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[128];
            var read = stream.Read(header, 0, header.Length);
            if (read < 128) return false;
            if (header[0] != (byte)'D' || header[1] != (byte)'D' || header[2] != (byte)'S' || header[3] != (byte)' ')
                return false;
            var height = BitConverter.ToInt32(header, 12);
            var width = BitConverter.ToInt32(header, 16);
            if (width <= 0 || height <= 0) return false;
            return !IsPowerOfTwo(width) || !IsPowerOfTwo(height);
        }
        catch { return false; }
    }

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    // DDS_HEADER.dwCaps2 flags (file offset 112 — right after the 4-byte
    // "DDS " magic + the 108-byte DDS_HEADER up through dwCaps; cross-checked
    // against this file's own already-correct fourCC read at file offset 84,
    // which sits inside the 32-byte ddspf sub-struct starting at 76).
    private const int DdsCaps2Cubemap = 0x200;
    private const int DdsCaps2Volume = 0x200000;

    // ddspf (DDS_PIXELFORMAT).dwFlags bits — file offset 80 (ddspf starts
    // at 76: dwSize@76, dwFlags@80, dwFourCC@84, dwRGBBitCount@88,
    // dwRBitMask@92, dwGBitMask@96, dwBBitMask@100, dwABitMask@104).
    private const int DdpfAlphaPixels = 0x1;
    private const int DdpfAlpha = 0x2;
    private const int DdpfFourCc = 0x4;
    private const int DdpfRgb = 0x40;
    private const int DdpfLuminance = 0x20000;

    /// <summary>Some real vanilla DDS variants can't be handed to Unity's
    /// TextureFormat enum directly — either because Unity has no native
    /// compressed-format value for them (DXT3/BC2) or because they're an
    /// uncompressed single/dual-channel layout (Luminance/Alpha-only) this
    /// codebase previously didn't decode at all. For all of these, the real
    /// per-pixel bytes are decoded into a plain RGBA32 buffer in software
    /// (see TryBuildDdsFlat) instead — `format` in TryReadDdsHeader's output
    /// is set to RGBA32 (the eventual Unity format) whenever this isn't
    /// None; the SOURCE byte layout to decode from is determined by this
    /// value, not by `format`.</summary>
    private enum DdsSpecialDecode { None, Dxt3, Luminance8, LuminanceAlpha16, Alpha8 }

    private static bool TryReadDdsHeader(byte[] bytes, out int width, out int height, out int depth, out int mipCount, out TextureFormat format, out int caps2, out DdsSpecialDecode specialDecode, out string error)
    {
        width = height = depth = 0;
        mipCount = 1;
        format = TextureFormat.DXT1;
        caps2 = 0;
        specialDecode = DdsSpecialDecode.None;
        error = null;
        if (bytes.Length < 128) { error = "DDS header is truncated."; return false; }
        height = BitConverter.ToInt32(bytes, 12);
        width = BitConverter.ToInt32(bytes, 16);
        depth = Math.Max(1, BitConverter.ToInt32(bytes, 24));
        mipCount = Math.Max(1, BitConverter.ToInt32(bytes, 28));
        caps2 = BitConverter.ToInt32(bytes, 112);

        var pixelFormatFlags = BitConverter.ToInt32(bytes, 80);
        if ((pixelFormatFlags & DdpfFourCc) != 0)
        {
            var fourCc = System.Text.Encoding.ASCII.GetString(bytes, 84, 4);
            if (fourCc == "DXT1") format = TextureFormat.DXT1;
            else if (fourCc == "DXT5") format = TextureFormat.DXT5;
            else if (fourCc == "DXT3") { format = TextureFormat.RGBA32; specialDecode = DdsSpecialDecode.Dxt3; }
            else { error = $"Only DXT1/DXT3/DXT5-compressed or standard uncompressed DDS files are supported (found FourCC '{fourCc}')."; return false; }
        }
        else if ((pixelFormatFlags & DdpfRgb) != 0)
        {
            // Real volume LUT files (ColorCorrectionCube/FogOfWarCorrectionCube)
            // are NOT DXT-compressed at all — confirmed against a real vanilla
            // file: 64x64x64, "Bitmasked", 32bpp, R=0xff0000 G=0xff00 B=0xff
            // A=0xff000000. That's exactly Unity's BGRA32 byte order (little-
            // endian 0x00RRGGBB, alpha in the high byte) — the standard,
            // overwhelmingly common uncompressed DDS layout. Only this one
            // exact layout is handled; anything else uncompressed errors
            // clearly rather than silently misreading bytes.
            var bitCount = BitConverter.ToInt32(bytes, 88);
            var rMask = BitConverter.ToUInt32(bytes, 92);
            var gMask = BitConverter.ToUInt32(bytes, 96);
            var bMask = BitConverter.ToUInt32(bytes, 100);
            var aMask = BitConverter.ToUInt32(bytes, 104);
            if (bitCount == 32 && rMask == 0x00ff0000u && gMask == 0x0000ff00u && bMask == 0x000000ffu && aMask == 0xff000000u)
            {
                format = TextureFormat.BGRA32;
            }
            else
            {
                error = $"Unsupported uncompressed DDS pixel layout (bitCount={bitCount}). Only DXT1/DXT3/DXT5-compressed or standard 32-bit BGRA uncompressed DDS files are supported.";
                return false;
            }
        }
        else if ((pixelFormatFlags & DdpfLuminance) != 0)
        {
            // Single/dual-channel lookup-table style textures (e.g. a real
            // vanilla 'horizonLookup.dds') — 8bpp grayscale, or 16bpp
            // grayscale+alpha when DDPF_ALPHAPIXELS is also set.
            var bitCount = BitConverter.ToInt32(bytes, 88);
            var hasAlpha = (pixelFormatFlags & DdpfAlphaPixels) != 0;
            format = TextureFormat.RGBA32;
            if (bitCount == 8 && !hasAlpha) specialDecode = DdsSpecialDecode.Luminance8;
            else if (bitCount == 16 && hasAlpha) specialDecode = DdsSpecialDecode.LuminanceAlpha16;
            else { error = $"Unsupported luminance DDS layout (bitCount={bitCount}, hasAlphaPixels={hasAlpha})."; return false; }
        }
        else if ((pixelFormatFlags & DdpfAlpha) != 0)
        {
            var bitCount = BitConverter.ToInt32(bytes, 88);
            if (bitCount == 8) { format = TextureFormat.RGBA32; specialDecode = DdsSpecialDecode.Alpha8; }
            else { error = $"Unsupported alpha-only DDS layout (bitCount={bitCount})."; return false; }
        }
        else
        {
            error = $"Unsupported DDS pixel format (flags=0x{pixelFormatFlags:X}, neither FourCC-compressed, RGB, luminance, nor alpha-only).";
            return false;
        }

        if (width <= 0 || height <= 0) { error = "DDS dimensions are invalid."; return false; }
        return true;
    }

    private static byte[] DecodeDxt3ToRgba32(byte[] blockData, int width, int height)
    {
        var result = new byte[width * height * 4];
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        var offset = 0;
        for (var by = 0; by < blocksHigh; by++)
        {
            for (var bx = 0; bx < blocksWide; bx++)
            {
                var alphaBytes = blockData; var alphaOffset = offset;
                offset += 8;
                var c0 = (ushort)(blockData[offset] | (blockData[offset + 1] << 8));
                var c1 = (ushort)(blockData[offset + 2] | (blockData[offset + 3] << 8));
                var indices = BitConverter.ToUInt32(blockData, offset + 4);
                offset += 8;

                var col0 = Rgb565ToRgb888(c0);
                var col1 = Rgb565ToRgb888(c1);
                var pal2 = ((byte)((2 * col0.r + col1.r) / 3), (byte)((2 * col0.g + col1.g) / 3), (byte)((2 * col0.b + col1.b) / 3));
                var pal3 = ((byte)((col0.r + 2 * col1.r) / 3), (byte)((col0.g + 2 * col1.g) / 3), (byte)((col0.b + 2 * col1.b) / 3));

                for (var py = 0; py < 4; py++)
                {
                    var alphaRow = (ushort)(alphaBytes[alphaOffset + py * 2] | (alphaBytes[alphaOffset + py * 2 + 1] << 8));
                    for (var px = 0; px < 4; px++)
                    {
                        var x = bx * 4 + px; var y = by * 4 + py;
                        if (x >= width || y >= height) continue;
                        var idx = (int)((indices >> (2 * (py * 4 + px))) & 0x3);
                        var (r, g, b) = idx == 0 ? col0 : idx == 1 ? col1 : idx == 2 ? pal2 : pal3;
                        var a4 = (int)((alphaRow >> (4 * px)) & 0xF);
                        var pixelIndex = (y * width + x) * 4;
                        result[pixelIndex + 0] = r;
                        result[pixelIndex + 1] = g;
                        result[pixelIndex + 2] = b;
                        result[pixelIndex + 3] = (byte)(a4 * 17); // 0..15 -> 0..255
                    }
                }
            }
        }
        return result;
    }

    private static (byte r, byte g, byte b) Rgb565ToRgb888(ushort c)
    {
        var r5 = (c >> 11) & 0x1F;
        var g6 = (c >> 5) & 0x3F;
        var b5 = c & 0x1F;
        return ((byte)((r5 << 3) | (r5 >> 2)), (byte)((g6 << 2) | (g6 >> 4)), (byte)((b5 << 3) | (b5 >> 2)));
    }

    private static byte[] ExpandSingleOrDualChannelToRgba32(byte[] raw, int width, int height, DdsSpecialDecode kind)
    {
        var result = new byte[width * height * 4];
        var pixelCount = width * height;
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            switch (kind)
            {
                case DdsSpecialDecode.Luminance8:
                    var lum = raw[i];
                    result[o] = lum; result[o + 1] = lum; result[o + 2] = lum; result[o + 3] = 255;
                    break;
                case DdsSpecialDecode.LuminanceAlpha16:
                    var l = raw[i * 2]; var a = raw[i * 2 + 1];
                    result[o] = l; result[o + 1] = l; result[o + 2] = l; result[o + 3] = a;
                    break;
                case DdsSpecialDecode.Alpha8:
                    result[o] = 255; result[o + 1] = 255; result[o + 2] = 255; result[o + 3] = raw[i];
                    break;
            }
        }
        return result;
    }

    /// <summary>Bytes/pixel (or bytes/block-equivalent) of the SOURCE
    /// payload for a special-decode kind — distinct from MipLevelByteSize,
    /// which is keyed on the FINAL Unity format (always RGBA32 once a
    /// special decode applies), not the source layout.</summary>
    private static int SourceMipLevelByteSize(int width, int height, DdsSpecialDecode specialDecode) => specialDecode switch
    {
        DdsSpecialDecode.Dxt3 => MipLevelByteSize(width, height, TextureFormat.DXT5), // BC2/DXT3 is 16 bytes/block, identical to DXT5's formula.
        DdsSpecialDecode.LuminanceAlpha16 => width * height * 2,
        _ => width * height, // Luminance8 / Alpha8: 1 byte/pixel.
    };

    private static bool IsCompressedFormat(TextureFormat format) => format == TextureFormat.DXT1 || format == TextureFormat.DXT5;
    private static int BlockSize(TextureFormat format) => format == TextureFormat.DXT1 ? 8 : 16;

    /// <summary>Real DDS files store row 0 at the TOP of the image, but
    /// Unity's Texture2D/SetPixelData expects row 0 at the BOTTOM (matching
    /// its own GetPixel/UV convention) — confirmed by comparing this
    /// decoder's output against an external DDS viewer for real vanilla
    /// terrain textures (mat_buildings/mat_shiny Diffuse1) and against
    /// Unity's own native DDS importer, which performs this compensation
    /// internally. Copying DDS bytes straight through (as this decoder did
    /// before this fix) therefore produced every texture it decoded
    /// vertically mirrored versus a correct viewer or Unity's native import.
    /// A block-compressed (DXT1/DXT5) texture can't be flipped by reversing
    /// pixel rows directly — each 4x4 block packs 4 rows' worth of index
    /// data into a handful of bytes — so this requires BOTH (1) swapping
    /// whole blocks between block-row `by` and block-row
    /// `blocksHigh-1-by`, AND (2) re-ordering each swapped block's own
    /// internal 4 rows. Doing only (1) scrambles the image into
    /// noise/banding rather than cleanly mirroring it — almost certainly
    /// what happened in two earlier, reverted attempts at this exact fix
    /// (9142aad -> e2b4068's history), which is why this is implemented as
    /// an explicit per-format bit-level block flip instead of a generic byte
    /// reversal.
    ///
    /// SC2Water.shader's Mask1 sampler has its own compensating
    /// `depthSample.y = 1.0 - depthSample.y` flip (592d07a). That flip was
    /// removed for a while (2026-08-16) on the theory that decoding
    /// correctly here made it a redundant double-flip against this method's
    /// own fix — direct in-game testing (2026-08-23) disproved that and it
    /// was restored. _Mask1Tex (loaded via LoadTexturePublicRawData's
    /// forceDefaultType:true) does route through this same decoder, so if
    /// this method's own flip logic is ever revisited, re-check Mask1's
    /// shader-side flip alongside it rather than assuming the two always
    /// cancel out.</summary>
    private static byte[] FlipCompressedBlocksVertically(byte[] data, int width, int height, TextureFormat format)
    {
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        var blockSize = BlockSize(format);
        var result = new byte[data.Length];
        var srcBlock = new byte[blockSize];
        var dstBlock = new byte[blockSize];
        for (var by = 0; by < blocksHigh; by++)
        {
            var dstRow = blocksHigh - 1 - by;
            for (var bx = 0; bx < blocksWide; bx++)
            {
                var srcOffset = (by * blocksWide + bx) * blockSize;
                var dstOffset = (dstRow * blocksWide + bx) * blockSize;
                Buffer.BlockCopy(data, srcOffset, srcBlock, 0, blockSize);
                FlipBlockRows(srcBlock, dstBlock, format);
                Buffer.BlockCopy(dstBlock, 0, result, dstOffset, blockSize);
            }
        }
        return result;
    }

    /// <summary>Reorders one block's internal 4 pixel-rows (row0&lt;-&gt;row3,
    /// row1&lt;-&gt;row2) without touching its endpoint colors/alphas.</summary>
    private static void FlipBlockRows(byte[] src, byte[] dst, TextureFormat format)
    {
        if (format == TextureFormat.DXT1)
        {
            // Bytes 0-3: color0/color1 (unchanged). Bytes 4-7: one byte per
            // pixel-row (2-bit index x4 pixels) — reverse row order.
            dst[0] = src[0]; dst[1] = src[1]; dst[2] = src[2]; dst[3] = src[3];
            dst[4] = src[7]; dst[5] = src[6]; dst[6] = src[5]; dst[7] = src[4];
            return;
        }
        // DXT5: byte 0-1 alpha0/alpha1 (unchanged), bytes 2-7 = 48-bit alpha
        // index table (16 pixels x 3 bits — row-flipped below), bytes 8-15 =
        // a plain DXT1-shaped color block (colors at 8-11, index rows at
        // 12-15 — flipped the same way as the DXT1 case above).
        dst[0] = src[0]; dst[1] = src[1];
        FlipDxt5AlphaIndexRows(src, dst);
        dst[8] = src[8]; dst[9] = src[9]; dst[10] = src[10]; dst[11] = src[11];
        dst[12] = src[15]; dst[13] = src[14]; dst[14] = src[13]; dst[15] = src[12];
    }

    /// <summary>DXT5's 6-byte alpha index table packs 16 pixels' 3-bit
    /// indices as two 24-bit little-endian integers (bytes 2-4 = pixels
    /// 0-7/rows 0-1, bytes 5-7 = pixels 8-15/rows 2-3, 4 pixels per row,
    /// LSB-first). Unpacks all 16, swaps row0&lt;-&gt;row3 and row1&lt;-&gt;row2,
    /// and repacks the same way.</summary>
    private static void FlipDxt5AlphaIndexRows(byte[] src, byte[] dst)
    {
        var bits0 = (uint)(src[2] | (src[3] << 8) | (src[4] << 16));
        var bits1 = (uint)(src[5] | (src[6] << 8) | (src[7] << 16));
        var idx = new uint[16];
        for (var i = 0; i < 8; i++) idx[i] = (bits0 >> (3 * i)) & 0x7;
        for (var i = 0; i < 8; i++) idx[8 + i] = (bits1 >> (3 * i)) & 0x7;
        var flipped = new uint[16];
        for (var row = 0; row < 4; row++)
            for (var col = 0; col < 4; col++)
                flipped[row * 4 + col] = idx[(3 - row) * 4 + col];
        uint newBits0 = 0, newBits1 = 0;
        for (var i = 0; i < 8; i++) newBits0 |= flipped[i] << (3 * i);
        for (var i = 0; i < 8; i++) newBits1 |= flipped[8 + i] << (3 * i);
        dst[2] = (byte)(newBits0 & 0xFF); dst[3] = (byte)((newBits0 >> 8) & 0xFF); dst[4] = (byte)((newBits0 >> 16) & 0xFF);
        dst[5] = (byte)(newBits1 & 0xFF); dst[6] = (byte)((newBits1 >> 8) & 0xFF); dst[7] = (byte)((newBits1 >> 16) & 0xFF);
    }

    /// <summary>Same DDS-row-0-is-top-but-Unity-wants-bottom fix as
    /// FlipCompressedBlocksVertically, for uncompressed pixel data (BGRA32,
    /// and the RGBA32 buffers ExpandSingleOrDualChannelToRgba32/
    /// DecodeDxt3ToRgba32 produce) — a plain whole-row swap, no block
    /// structure to preserve.</summary>
    private static byte[] FlipUncompressedRowsVertically(byte[] data, int width, int height, int bytesPerPixel)
    {
        var stride = width * bytesPerPixel;
        var result = new byte[data.Length];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(data, y * stride, result, (height - 1 - y) * stride, stride);
        return result;
    }

    private static int MipLevelByteSize(int width, int height, TextureFormat format)
    {
        if (!IsCompressedFormat(format))
            return width * height * 4; // BGRA32 — the only uncompressed format currently supported, 4 bytes/pixel.
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        return blocksWide * blocksHigh * BlockSize(format);
    }

    private static int MipChainByteSize(int width, int height, int mipCount, TextureFormat format)
    {
        var total = 0;
        var w = width; var h = height;
        for (var i = 0; i < mipCount; i++)
        {
            total += MipLevelByteSize(w, h, format);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        return total;
    }

    /// <summary>Single entry point for every supported source shape — flat
    /// 2D, cubemap (EnvironmentMap/SkyMap — confirmed real samplerCUBE
    /// usage in gamedata.scd/effects/terrain.hlsl,unit.hlsl,water.hlsl,
    /// sky.hlsl), and volume/3D LUT (ColorCorrectionCube/
    /// FogOfWarCorrectionCube — confirmed real sampler3D usage in
    /// gamedata.scd/effects/frame.hlsl; NOT a cubemap despite the field
    /// name, "Cube" here is the standard color-grading "LUT cube" term).
    /// `preview` is always a flat Texture2D suitable for direct drawing,
    /// regardless of which shape `texture` resolves to.</summary>
    /// <summary>Public so SC2TextureCatalogBuilder's vanilla-catalog scan
    /// can parse a real .dds file directly off disk for cubemap/volume
    /// shapes Unity's own native DDS importer refuses to recognize as any
    /// asset type at all (confirmed via a real vanilla file: an
    /// uncompressed-bitmasked volume DDS logged "Unsupported ... file" from
    /// Unity's own IHVImageFormats.cpp) — AssetDatabase.FindAssets can never
    /// see such a file since Unity never classified it as a Texture3D/
    /// Cubemap asset in the first place, so bypassing the AssetDatabase
    /// entirely and reading+parsing the bytes ourselves is the only way to
    /// catalog it.</summary>
    public static bool TryLoadBytesAny(byte[] bytes, string extension, out Texture texture, out Texture2D preview, out bool isCubemap, out bool isVolume, out string error, bool linear = false, bool disableMipmaps = false)
    {
        texture = null;
        preview = null;
        isCubemap = false;
        isVolume = false;
        error = null;
        if (IsDds(bytes))
        {
            if (!TryReadDdsHeader(bytes, out var width, out var height, out var depth, out var mipCount, out var format, out var caps2, out var specialDecode, out error))
                return false;
            if ((caps2 & DdsCaps2Cubemap) != 0)
            {
                // Every real cubemap in this game (EnvironmentMap/SkyMap) is
                // plain DXT1/DXT5 — DXT3/luminance/alpha-only cubemaps are
                // not a real shape this game uses, so fail clearly instead
                // of silently mishandling an unexpected combination.
                if (specialDecode != DdsSpecialDecode.None) { error = "Cubemap DDS with a non-DXT1/DXT5 pixel format is not supported."; return false; }
                isCubemap = true;
                if (!TryBuildDdsCubemap(bytes, width, mipCount, format, out var cubemap, out preview, out error)) return false;
                texture = cubemap;
                return true;
            }
            if ((caps2 & DdsCaps2Volume) != 0)
            {
                if (specialDecode != DdsSpecialDecode.None) { error = "Volume DDS with a non-DXT1/DXT5/BGRA32 pixel format is not supported."; return false; }
                isVolume = true;
                if (!TryBuildDdsVolume(bytes, width, height, depth, mipCount, format, out var volume, out preview, out error)) return false;
                texture = volume;
                return true;
            }
            if (!TryBuildDdsFlat(bytes, width, height, mipCount, format, specialDecode, linear, out var flat, out error, disableMipmaps)) return false;
            texture = flat;
            preview = flat;
            return true;
        }
        if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            error = "Supported formats are PNG, JPEG/JPG and DXT1/DXT5 DDS (flat, cubemap, or volume).";
            return false;
        }
        var tex2d = new Texture2D(2, 2, TextureFormat.RGBA32, true) { hideFlags = HideFlags.DontSave };
        if (ImageConversion.LoadImage(tex2d, bytes, false)) { texture = tex2d; preview = tex2d; return true; }
        UnityEngine.Object.Destroy(tex2d);
        error = "Unity could not decode the image.";
        return false;
    }

    /// <summary>DXT1/DXT5 require 4x4 block-aligned dimensions — Unity's
    /// Texture2D constructor logs a native console error (not a catchable
    /// C# exception, confirmed against a real batch of odd-dimension
    /// vanilla DDS files that spammed hundreds of near-identical
    /// "Compressed GraphicsFormat ... requires a texture size that is a
    /// multiple of 4" lines) rather than throwing, so the existing
    /// try/catch below never sees it. Checked before ever constructing the
    /// texture instead.</summary>
    private static bool IsBlockAligned(int width, int height, TextureFormat format) =>
        !IsCompressedFormat(format) || (width % 4 == 0 && height % 4 == 0);

    // Textures whose DDS file carries no authored mip chain (header mipCount
    // == 1, or a decode path that only ever builds level 0). For a COMPRESSED
    // format Unity cannot generate the missing levels on the CPU
    // (Texture2D.Apply(updateMipmaps: true) leaves them unfilled) even though
    // Texture2D.mipmapCount still reports a full chain, so sampling any LOD
    // above 0 from such a texture returns garbage — see BuildFilteredPreview,
    // which rebuilds the chain on the GPU for these. The terrain shader path
    // never hit this because the GPU builds its own mips there.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture, object> _unreliableMips = new();

    public static bool HasUnreliableMips(Texture texture) => texture != null && _unreliableMips.TryGetValue(texture, out _);

    private static Texture2D MarkUnreliableMips(Texture2D texture)
    {
        if (texture != null) _unreliableMips.GetValue(texture, _ => new object());
        return texture;
    }

    private static bool TryBuildDdsFlat(byte[] bytes, int width, int height, int mipCount, TextureFormat format, DdsSpecialDecode specialDecode, bool linear, out Texture2D texture, out string error, bool disableMipmaps = false)
    {
        texture = null;
        error = null;
        try
        {
            if (specialDecode != DdsSpecialDecode.None)
            {
                // DXT3/luminance/alpha-only sources have no embedded-mip-chain
                // convention this codebase relies on anywhere — real vanilla
                // uses of these (mapshots, lookup textures) are for browsing/
                // preview or a single flat sample, so only the top level is
                // decoded, matching TryLoad's own "always a flat drawable
                // Texture2D" contract.
                var sourceSize = SourceMipLevelByteSize(width, height, specialDecode);
                var source = new byte[sourceSize];
                Buffer.BlockCopy(bytes, 128, source, 0, sourceSize);
                var rgba = specialDecode == DdsSpecialDecode.Dxt3
                    ? DecodeDxt3ToRgba32(source, width, height)
                    : ExpandSingleOrDualChannelToRgba32(source, width, height, specialDecode);
                rgba = FlipUncompressedRowsVertically(rgba, width, height, 4);
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false, linear)
                    { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat };
                texture.LoadRawTextureData(rgba);
                texture.Apply(false, false);
                MarkUnreliableMips(texture);
                return true;
            }

            if (IsBlockAligned(width, height, format) && disableMipmaps)
            {
                // RNM basis-vector/blend-mask data must never be sampled
                // through any mip level, real embedded or Unity-generated —
                // box-filtering (averaging) raw basis-vector components does
                // not produce another valid unit basis vector, and averaging
                // a scalar blend factor across a compressed block's own
                // quantization introduces the periodic banding/"stripe"
                // pattern this fixes (confirmed on SC2_D3_Eclipse's real
                // vanilla RNM masks, per user direction 2026-08-16). Decode
                // ONLY mip level 0 and build a single-level texture, so the
                // GPU always samples full-resolution data regardless of view
                // distance — real embedded mip levels beyond 0 (if any) are
                // intentionally never read.
                //
                // Known trade-off, already once observed empirically in this
                // codebase (see the mipCount==1/Apply(true) comment just
                // below): disabling mips previously caused a hard visual
                // edge in the Diffuse1/Diffuse2 material blend, because RNM
                // Mask 1's ALPHA channel doubles as that blend factor and
                // benefits from mip-smoothed transitions. That regression was
                // observed via the narrower NPOT-only bypass this same
                // decoder implements; this fix intentionally accepts that
                // trade-off for RNM masks specifically, per explicit user
                // direction — revisit here first if a hard material-blend
                // edge reappears.
                var levelSize = MipLevelByteSize(width, height, format);
                var level = new byte[levelSize];
                Buffer.BlockCopy(bytes, 128, level, 0, levelSize);
                level = IsCompressedFormat(format)
                    ? FlipCompressedBlocksVertically(level, width, height, format)
                    : FlipUncompressedRowsVertically(level, width, height, 4);
                texture = new Texture2D(width, height, format, false, linear)
                    { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat };
                texture.SetPixelData(level, 0);
                texture.Apply(false, false);
                MarkUnreliableMips(texture);
                return true;
            }

            if (IsBlockAligned(width, height, format))
            {
                // Per-level SetPixelData (matching the already-correct
                // cubemap/volume decode below), NOT one bulk
                // LoadRawTextureData(wholePayload) call for the whole chain.
                // Confirmed via Unity's own Texture Inspector mip-level
                // preview (real vanilla NPOT file, mips below level 0 shown
                // as pure block noise there — not a rendering/shader issue)
                // that Unity's *native* DDS import mis-lays-out embedded
                // mips for non-power-of-two compressed textures; a bulk
                // LoadRawTextureData call for a multi-level texture relies
                // on that exact same internal "expected per-level layout"
                // Unity computes for itself, so it is not trustworthy either
                // for this class of file. SetPixelData(bytes, mip) instead
                // targets one explicit level directly, using only this
                // file's own already-correct MipLevelByteSize/ceil-4-block
                // math — no dependency on Unity's internal chain-layout
                // assumptions at all.
                // Real vanilla files are frequently single-mip (mipCount==1
                // in their own DDS header — e.g. a real RNM blend mask,
                // playsurface_1.win.dds, confirmed via NVIDIA Texture Tools).
                // Unity's *native* importer generates a full mip chain from
                // a single-level source by default ("Generate Mip Maps" is
                // on by default project-wide) — this decoder must match that
                // or a material sampling this texture at any non-zero mip
                // (i.e. almost always, at any real viewing distance) gets no
                // mip data at all instead of a properly-filtered downsample,
                // which showed up as a hard-edged intra-material Diffuse1/
                // Diffuse2 blend instead of the source mask's own smooth
                // gradient — confirmed by elimination (shader logic and mask
                // content were independently verified correct; reverting
                // just this decode path, with nothing else touched, is what
                // made the hard edge go away).
                //
                // Always allocate room for a full chain; for a real embedded
                // multi-level source (mipCount>1) every level is explicitly
                // populated below from the file's own bytes and Apply(false)
                // preserves them as-is (Unity must not regenerate over real
                // authored mip data). For a single-level source (mipCount==1)
                // only level 0 is populated and Apply(true) lets Unity
                // generate the rest from it, matching native-import behavior.
                texture = new Texture2D(width, height, format, true, linear)
                    { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat };
                var w = width; var h = height;
                var offset = 128;
                for (var mip = 0; mip < mipCount; mip++)
                {
                    var levelSize = MipLevelByteSize(w, h, format);
                    var level = new byte[levelSize];
                    Buffer.BlockCopy(bytes, offset, level, 0, levelSize);
                    level = IsCompressedFormat(format)
                        ? FlipCompressedBlocksVertically(level, w, h, format)
                        : FlipUncompressedRowsVertically(level, w, h, 4);
                    texture.SetPixelData(level, mip);
                    offset += levelSize;
                    w = Math.Max(1, w / 2);
                    h = Math.Max(1, h / 2);
                }
                texture.Apply(updateMipmaps: mipCount == 1, makeNoLongerReadable: false);
                if (mipCount == 1) MarkUnreliableMips(texture);
                return true;
            }

            // Real vanilla DXT1/DXT5 files are frequently NOT 4-aligned
            // (confirmed: 448x206 map-shot thumbnails, 1024x1025 terrain
            // masks) — the game's own renderer handles this fine (GPU
            // texture units always store compressed data in whole 4x4
            // blocks regardless of the logical width/height, per the
            // standard DDS/BC convention this file's own MipLevelByteSize
            // already uses for storage-size math), but Unity's C# Texture2D
            // constructor refuses non-aligned dimensions outright. Pad up to
            // the next block boundary and decode only the top level (whose
            // byte size, from MipLevelByteSize's own ceil-to-4 formula, is
            // exactly what a same-size padded, non-mipped compressed
            // texture expects) — a few pixels of padding at the edge are
            // invisible for this codebase's browsing/preview use of the
            // result, and avoids relying on Unity's own internal per-level
            // padding progression matching the file's real mip layout.
            var paddedWidth = ((width + 3) / 4) * 4;
            var paddedHeight = ((height + 3) / 4) * 4;
            var topLevelSize = MipLevelByteSize(width, height, format);
            var topLevel = new byte[topLevelSize];
            Buffer.BlockCopy(bytes, 128, topLevel, 0, topLevelSize);
            topLevel = IsCompressedFormat(format)
                ? FlipCompressedBlocksVertically(topLevel, width, height, format)
                : FlipUncompressedRowsVertically(topLevel, width, height, 4);
            texture = new Texture2D(paddedWidth, paddedHeight, format, false, linear)
                { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat };
            texture.LoadRawTextureData(topLevel);
            texture.Apply(false, false);
            MarkUnreliableMips(texture);
            return true;
        }
        catch (Exception ex)
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null;
            error = "DDS decode failed: " + ex.Message;
            return false;
        }
    }

    private static readonly CubemapFace[] DdsFaceOrder =
    {
        CubemapFace.PositiveX, CubemapFace.NegativeX,
        CubemapFace.PositiveY, CubemapFace.NegativeY,
        CubemapFace.PositiveZ, CubemapFace.NegativeZ,
    };

    private static bool TryBuildDdsCubemap(byte[] bytes, int size, int mipCount, TextureFormat format, out Cubemap cubemap, out Texture2D preview, out string error)
    {
        cubemap = null;
        preview = null;
        error = null;
        if (!IsBlockAligned(size, size, format))
        {
            error = $"DXT-compressed cubemap DDS size must be a multiple of 4 (got {size}).";
            return false;
        }
        var faceSize = MipChainByteSize(size, size, mipCount, format);
        if (bytes.Length < 128 + faceSize * 6) { error = "Cubemap DDS payload is smaller than expected — truncated file?"; return false; }
        var facePreviews = new Texture2D[6];
        try
        {
            cubemap = new Cubemap(size, format, mipCount > 1)
                { hideFlags = HideFlags.DontSave, filterMode = mipCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear };
            var faceOffset = 128;
            for (var f = 0; f < 6; f++)
            {
                var w = size; var h = size;
                var mipOffset = faceOffset;
                for (var mip = 0; mip < mipCount; mip++)
                {
                    var levelSize = MipLevelByteSize(w, h, format);
                    var level = new byte[levelSize];
                    Buffer.BlockCopy(bytes, mipOffset, level, 0, levelSize);
                    cubemap.SetPixelData(level, mip, DdsFaceOrder[f]);
                    if (mip == 0)
                    {
                        var faceTex = new Texture2D(w, h, format, false) { hideFlags = HideFlags.DontSave };
                        faceTex.LoadRawTextureData(level);
                        faceTex.Apply(false, false);
                        facePreviews[f] = faceTex;
                    }
                    mipOffset += levelSize;
                    w = Math.Max(1, w / 2);
                    h = Math.Max(1, h / 2);
                }
                faceOffset += faceSize;
            }
            cubemap.Apply(false, false);
            preview = ComposeCubeCrossPreview(facePreviews);
            return true;
        }
        catch (Exception ex)
        {
            if (cubemap != null) UnityEngine.Object.Destroy(cubemap);
            cubemap = null;
            error = "Cubemap DDS decode failed: " + ex.Message;
            return false;
        }
        finally
        {
            foreach (var t in facePreviews) if (t != null) UnityEngine.Object.Destroy(t);
        }
    }

    private static bool TryBuildDdsVolume(byte[] bytes, int width, int height, int depth, int mipCount, TextureFormat format, out Texture3D volume, out Texture2D preview, out string error)
    {
        volume = null;
        preview = null;
        error = null;
        if (!IsBlockAligned(width, height, format))
        {
            error = $"DXT-compressed volume DDS dimensions must be multiples of 4 (got {width}x{height}).";
            return false;
        }
        var w = width; var h = height; var d = depth;
        var totalSize = 0;
        for (var i = 0; i < mipCount; i++)
        {
            totalSize += MipLevelByteSize(w, h, format) * d;
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); d = Math.Max(1, d / 2);
        }
        if (bytes.Length < 128 + totalSize) { error = "Volume DDS payload is smaller than expected — truncated file?"; return false; }
        try
        {
            volume = new Texture3D(width, height, depth, format, mipCount > 1)
                { hideFlags = HideFlags.DontSave, filterMode = mipCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var offset = 128;
            byte[] middleSlice = null; var sliceW = width; var sliceH = height;
            w = width; h = height; d = depth;
            for (var mip = 0; mip < mipCount; mip++)
            {
                var sliceSize = MipLevelByteSize(w, h, format);
                var levelSize = sliceSize * d;
                var level = new byte[levelSize];
                Buffer.BlockCopy(bytes, offset, level, 0, levelSize);
                volume.SetPixelData(level, mip);
                if (mip == 0)
                {
                    var midIndex = d / 2;
                    middleSlice = new byte[sliceSize];
                    Buffer.BlockCopy(level, midIndex * sliceSize, middleSlice, 0, sliceSize);
                    sliceW = w; sliceH = h;
                }
                offset += levelSize;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); d = Math.Max(1, d / 2);
            }
            volume.Apply(false, false);
            if (middleSlice != null)
            {
                preview = new Texture2D(sliceW, sliceH, format, false) { hideFlags = HideFlags.DontSave };
                preview.LoadRawTextureData(middleSlice);
                preview.Apply(false, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            if (volume != null) UnityEngine.Object.Destroy(volume);
            volume = null;
            if (preview != null) UnityEngine.Object.Destroy(preview);
            preview = null;
            error = "Volume DDS decode failed: " + ex.Message;
            return false;
        }
    }

    /// <summary>Tiles up to cols*rows source textures into one small
    /// composite (a "cross-lite" grid for a cubemap's 6 faces) via the same
    /// Blit/ReadPixels pattern WriteThumbnail already uses below — works
    /// regardless of whether the sources are CPU-readable, since Blit
    /// samples them on the GPU. Public so SC2TextureCatalogBuilder's own
    /// vanilla-asset cubemap scan can bake the same style of preview
    /// without duplicating this compositing logic.</summary>
    public static Texture2D ComposeGridPreview(Texture2D[] tiles, int cols, int rows)
    {
        const int tileSize = 96;
        var composite = new Texture2D(tileSize * cols, tileSize * rows, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
        var target = RenderTexture.GetTemporary(tileSize, tileSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        try
        {
            for (var i = 0; i < tiles.Length && i < cols * rows; i++)
            {
                var tile = tiles[i];
                if (tile == null) continue;
                Graphics.Blit(tile, target);
                RenderTexture.active = target;
                var x = (i % cols) * tileSize;
                var y = (i / cols) * tileSize;
                composite.ReadPixels(new Rect(0, 0, tileSize, tileSize), x, y, false);
                // Same D3D render-to-texture readback flip as
                // BuildFilteredPreview/WriteThumbnail — flips only THIS
                // tile's own footprint in place (never the whole composite,
                // which would relocate tiles the (x,y) placement above
                // already put in the right slot).
                if (SystemInfo.graphicsUVStartsAtTop)
                    FlipCompositeTileInPlace(composite, x, y, tileSize);
            }
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
        composite.Apply(false, false);
        return composite;
    }

    /// <summary>Vertically flips one tileSize x tileSize block of
    /// `composite`, in place, at its own (x, y) position — see
    /// ComposeGridPreview/ComposeCubeCrossPreview's own comments for why a
    /// per-tile flip is needed instead of one flip over the whole
    /// composite.</summary>
    private static void FlipCompositeTileInPlace(Texture2D composite, int x, int y, int tileSize)
    {
        var pixels = composite.GetPixels(x, y, tileSize, tileSize);
        var flipped = new Color[pixels.Length];
        for (var row = 0; row < tileSize; row++)
            Array.Copy(pixels, row * tileSize, flipped, (tileSize - 1 - row) * tileSize, tileSize);
        composite.SetPixels(x, y, tileSize, tileSize, flipped);
    }

    // Cross-layout cell positions (col, row — row 0 is the visual top),
    // indexed by face order 0=+X,1=-X,2=+Y,3=-Y,4=+Z,5=-Z (matches
    // TryBuildDdsCubemap's DdsFaceOrder / SC2TextureCatalogBuilder's
    // BuildCubemapPreview face-index order). +Y (up) sits above +Z, -Y
    // (down) sits below it, and -X/+Z/+X/-Z form the horizontal band —
    // the standard "horizontal cross" skybox unwrap.
    private static readonly (int col, int row)[] CubeCrossLayout =
    {
        (2, 1), (0, 1), (1, 0), (1, 2), (1, 1), (3, 1),
    };

    /// <summary>Composites a cubemap's 6 faces into the standard unfolded
    /// "horizontal cross" layout (4 tiles wide, 3 tiles tall, 6 of the 12
    /// cells used) instead of a dense grid — reads as a recognizable
    /// skybox/environment unwrap rather than an arbitrary tile pack. Unused
    /// cells stay fully transparent. Same per-tile Blit+ReadPixels
    /// mechanism as ComposeGridPreview, just placed at fixed cross
    /// positions instead of packed left-to-right.</summary>
    public static Texture2D ComposeCubeCrossPreview(Texture2D[] faces, int tileSize = 96)
    {
        const int cols = 4, rows = 3;
        var composite = new Texture2D(tileSize * cols, tileSize * rows, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
        composite.SetPixels32(new Color32[tileSize * cols * tileSize * rows]);
        var target = RenderTexture.GetTemporary(tileSize, tileSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        try
        {
            for (var i = 0; i < faces.Length && i < CubeCrossLayout.Length; i++)
            {
                var tile = faces[i];
                if (tile == null) continue;
                Graphics.Blit(tile, target);
                RenderTexture.active = target;
                var (col, row) = CubeCrossLayout[i];
                var x = col * tileSize;
                // Texture2D pixel origin is bottom-left, but `row` is
                // top-relative (row 0 = visual top) — flip.
                var y = (rows - 1 - row) * tileSize;
                composite.ReadPixels(new Rect(0, 0, tileSize, tileSize), x, y, false);
                // Same per-tile D3D readback flip as ComposeGridPreview.
                if (SystemInfo.graphicsUVStartsAtTop)
                    FlipCompositeTileInPlace(composite, x, y, tileSize);
            }
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
        composite.Apply(false, false);
        return composite;
    }

    private static byte[] EncodeMaterialDds(Texture2D texture)
    {
        // GetPixels32 returns Unity's native bottom-up row order;
        // SC2DdsEncoder.BuildMaterialDds (like BuildDxt1SingleMip/BuildBgra32
        // in SC2MapPreviewCapture) expects DDS's top-down order and never
        // flips internally — without this, a custom texture re-encoded here
        // (any non-DDS import, see Import above) came out vertically flipped
        // relative to the DDS this same class's own read-side decode (which
        // DOES flip, see FlipUncompressedRowsVertically/
        // FlipCompressedBlocksVertically) expects, and relative to every
        // real archive-sourced DDS.
        var pixels = FlipPixelRows(texture.GetPixels32(), texture.width, texture.height);
        var rgb = new byte[pixels.Length * 3];
        var alpha = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            rgb[i * 3] = pixels[i].r;
            rgb[i * 3 + 1] = pixels[i].g;
            rgb[i * 3 + 2] = pixels[i].b;
            alpha[i] = pixels[i].a;
        }
        return SC2DdsEncoder.BuildMaterialDds(texture.width, texture.height, rgb, alpha);
    }

    /// <summary>Row-major vertical flip for a Color32[] grid — shared by
    /// every BuildMaterialDds/BuildDxt5 caller in this file that starts from
    /// Unity's native (bottom-up) GetPixels32 order. See EncodeMaterialDds's
    /// own comment for why this flip is required at all.</summary>
    private static Color32[] FlipPixelRows(Color32[] pixels, int width, int height)
    {
        var flipped = new Color32[pixels.Length];
        for (var y = 0; y < height; y++)
            Array.Copy(pixels, y * width, flipped, (height - 1 - y) * width, width);
        return flipped;
    }

    /// <summary>Renders `source` down to a small, properly GPU-filtered
    /// RGBA32 preview via Graphics.Blit (same Blit+ReadPixels technique
    /// WriteThumbnail/ComposeGridPreview already use elsewhere in this
    /// file) — a real GPU texture sample, which correctly minifies using
    /// the source's own mip chain/filterMode, unlike a raw GUI.DrawTexture
    /// blit of the full-resolution texture into a small on-screen rect.
    /// Needed specifically for real vanilla RNM masks: their fine,
    /// high-frequency radial/directional-lighting detail (confirmed via
    /// direct decode of real files) aliases into a regular banded/striped
    /// pattern when squeezed into a ~150px preview box without this
    /// filtering — reported by the user as "RNM masks display incorrectly"
    /// even after the mesh/3D rendering (which samples through the GPU's
    /// normal mip pipeline, not affected) was already confirmed correct.
    ///
    /// A plain Graphics.Blit alone turned out NOT to be enough for a real
    /// map (SC2_MP_002's mask01_0, the one real file in the whole 56-map
    /// corpus with a genuine embedded 11-level mip chain rather than a
    /// single level Unity auto-generates on Apply) — the stripe artifact
    /// came back specifically for it. Independently decoded every mip level
    /// of that exact file with a standalone Python DXT5 decoder (bypassing
    /// this whole C# pipeline entirely) and confirmed every level, from mip
    /// 0 down to mip 3, decodes to the same clean radial pattern with no
    /// striping at all — so the raw bytes and this file's own per-level
    /// block-flip are correct; Graphics.Blit's default single-pass sampling
    /// (whatever internal shader/LOD heuristic it uses for a full-screen
    /// quad) was simply not reliably engaging the source's own mip chain to
    /// pick one of those already-correct lower levels.
    ///
    /// CORRECTION (2026-08-16): the original fix for that forced an explicit
    /// mip via `source.mipMapBias` before the blit, restored in a `finally`.
    /// That mutation is GLOBAL to the source Texture2D ASSET, not scoped to
    /// this one blit call — this preview box is very often drawing the exact
    /// same Texture2D object a real terrain Material's _Diffuse1/_Diffuse2
    /// slot also references (e.g. the Imported-terrain Materials panel draws
    /// both side by side). Confirmed the real symptom this caused: the
    /// actual 3D-rendered terrain surface itself appearing blurred (as if
    /// permanently sampling a biased/higher mip), which "fixed itself" when
    /// toggling an unrelated Shader Setup checkbox — that toggle just forced
    /// a repaint/material refresh that happened to land after this
    /// function's own `finally` had already restored bias to 0, not a real
    /// fix of anything. Switched to Hidden/SC2/BlitExplicitLod — a one-pass
    /// shader sampling via `tex2Dlod` with an explicit per-draw LOD — which
    /// achieves the identical "read real, already-antialiased mip data"
    /// result without ever touching the source asset's own mipMapBias field,
    /// so no other renderer sharing that asset can observe any transient
    /// state from this call.</summary>
    private static Material _blitExplicitLodMaterial;

    private static Material GetBlitExplicitLodMaterial()
    {
        if (_blitExplicitLodMaterial != null) return _blitExplicitLodMaterial;
        var shader = Resources.Load<Shader>("SC2BlitExplicitLod")
#if UNITY_EDITOR
                     ?? Shader.Find("Hidden/SC2/BlitExplicitLod")
#endif
                     ;
        if (shader == null) return null;
        _blitExplicitLodMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
        return _blitExplicitLodMaterial;
    }

    /// <summary>`linear`: true for normal maps/RNM masks — same raw
    /// vector/blend data TryLoad's own `linear` flag protects.
    ///
    /// The write-side gamma handling is driven by GL.sRGBWrite around the
    /// Blit call, NOT by the temporary RenderTexture's own
    /// RenderTextureReadWrite mode (a prior version used the latter).
    /// Confirmed real via diagnostic logging (SC2ColorDiag, 2026-09-18):
    /// re-picking the same real Diffuse texture through the browser a
    /// second time in the same session produced a filtered preview roughly
    /// the SQUARE of the correct value (e.g. avg 0.575 source -> 0.292
    /// filtered, instead of ~0.576) — the exact signature of an extra
    /// gamma-encode being applied — while the raw decoded source stayed
    /// byte-identical both times. RenderTexture.GetTemporary pools RTs by
    /// descriptor including readWrite mode, but is not guaranteed to
    /// re-tag a reused pooled RT's actual sRGB write behavior consistently
    /// across repeated calls with the same size/format — GL.sRGBWrite is
    /// Unity's own low-level, per-draw-call control for exactly this,
    /// independent of any RenderTexture's own cached format/pool state.</summary>
    public static Texture2D BuildFilteredPreview(Texture source, int size = 160, bool linear = false)
    {
        if (source == null) return null;
        var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        var previousSRGBWrite = GL.sRGBWrite;
        Texture2D result = null;
        RenderTexture mipSource = null;
        try
        {
            GL.sRGBWrite = !linear;
            var sourceWidth = Mathf.Max(1, source.width);
            var desiredLod = Mathf.Log(Mathf.Max(1f, (float)sourceWidth / size), 2f);

            // A texture with no authored mip chain (see HasUnreliableMips)
            // has nothing valid above level 0 to sample at desiredLod, so
            // sampling it straight reads garbage — visible as regular
            // stripes in the browser grid/details and the Materials panel's
            // slot preview. Copy level 0 into a render texture and let the
            // GPU build a real chain there instead, then sample THAT at the
            // same LOD. Explicit graphics formats (not a pooled
            // RenderTextureReadWrite) so the sRGB handling is unambiguous:
            // an sRGB target for color, plain UNorm for linear data
            // (normals/RNM), matching GL.sRGBWrite above.
            Texture sampleFrom = source;
            if (sourceWidth > size && HasUnreliableMips(source))
            {
                mipSource = new RenderTexture(source.width, source.height, 0,
                    linear ? UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm
                           : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB)
                {
                    useMipMap = true,
                    autoGenerateMips = false,
                    hideFlags = HideFlags.DontSave,
                };
                mipSource.Create();
                Graphics.Blit(source, mipSource);
                mipSource.GenerateMips();
                sampleFrom = mipSource;
            }

            var blitMat = GetBlitExplicitLodMaterial();
            if (blitMat != null)
            {
                blitMat.SetFloat("_Lod", desiredLod);
                Graphics.Blit(sampleFrom, target, blitMat);
            }
            else
            {
                // Shader failed to load — fall back to a plain blit (mip 0
                // aliasing risk, but no shared-asset-state mutation either
                // way) rather than silently producing a blank preview.
                Graphics.Blit(sampleFrom, target);
            }
            RenderTexture.active = target;
            result = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            result.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
            result.Apply(false, false);

            // Graphics.Blit into a RenderTexture, then ReadPixels, reads
            // back vertically flipped on APIs whose texture origin is
            // top-left (D3D/Metal/Vulkan — SystemInfo.graphicsUVStartsAtTop
            // == true, true on Windows' default DX backend) — same root
            // cause as SC2MapPreviewCapture.RenderOffscreen's identical fix.
            // This is why only entries with NO baked atlas thumbnail (which
            // fall through SC2TextureThumbnailCache.Load to this function)
            // showed an inverted browser preview — a pre-baked
            // ThumbnailResourcePath never goes through this Blit+ReadPixels
            // path at all.
            if (SystemInfo.graphicsUVStartsAtTop)
            {
                var flipped = FlipUncompressedRowsVertically(result.GetRawTextureData(), size, size, 4);
                result.LoadRawTextureData(flipped);
                result.Apply(false, false);
            }
        }
        finally
        {
            GL.sRGBWrite = previousSRGBWrite;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (mipSource != null)
            {
                mipSource.Release();
                UnityEngine.Object.Destroy(mipSource);
            }
        }
        return result;
    }

    public static string ReadEncoding(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 88) return "DDS";
            stream.Position = 84;
            var fourCc = new byte[4];
            stream.Read(fourCc, 0, 4);
            return System.Text.Encoding.ASCII.GetString(fourCc);
        }
        catch { return "DDS"; }
    }

    public static bool TryReadMetadata(string texturePath, out SC2CustomTextureMetadata metadata)
    {
        metadata = null;
        try
        {
            var path = texturePath + ".sc2tex.json";
            if (!File.Exists(path)) return false;
            metadata = JsonUtility.FromJson<SC2CustomTextureMetadata>(File.ReadAllText(path));
            return metadata != null && metadata.Version == 1;
        }
        catch { return false; }
    }

    private static bool HasUsefulAlpha(Texture texture)
    {
        // Cubemap/Texture3D: no cheap CPU-side pixel scan available here —
        // assume alpha may matter rather than silently reporting none.
        if (texture is not Texture2D tex2d) return true;
        try { foreach (var pixel in tex2d.GetPixels32()) if (pixel.a < 250) return true; }
        catch { }
        return false;
    }

    private static bool IsDds(byte[] bytes) => bytes != null && bytes.Length >= 4 && bytes[0] == 'D' && bytes[1] == 'D' && bytes[2] == 'S' && bytes[3] == ' ';
    private static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static string Sanitize(string name) { foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_'); return string.IsNullOrWhiteSpace(name) ? "texture" : name; }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }

    private static void WriteThumbnail(Texture2D source, string path)
    {
        var target = RenderTexture.GetTemporary(128, 128, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        Texture2D thumbnail = null;
        try
        {
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            thumbnail = new Texture2D(128, 128, TextureFormat.RGBA32, false, false);
            thumbnail.ReadPixels(new Rect(0, 0, 128, 128), 0, 0, false);
            thumbnail.Apply(false, false);
            // Same D3D render-to-texture readback flip as BuildFilteredPreview
            // — every baked custom-texture thumbnail (written here on import)
            // came out vertically flipped on Windows without this.
            if (SystemInfo.graphicsUVStartsAtTop)
            {
                var flipped = FlipUncompressedRowsVertically(thumbnail.GetRawTextureData(), 128, 128, 4);
                thumbnail.LoadRawTextureData(flipped);
                thumbnail.Apply(false, false);
            }
            WriteAtomic(path, thumbnail.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (thumbnail != null) UnityEngine.Object.Destroy(thumbnail);
        }
    }

    private static void WriteMetadata(string texturePath, SC2TextureCatalogEntry entry)
    {
        var metadata = new SC2CustomTextureMetadata
        {
            SourceHash = entry.SourceHash,
            ContentHash = entry.ContentHash,
            Usage = entry.Usage,
            Width = entry.Width,
            Height = entry.Height,
            MipCount = entry.MipCount,
            HasAlpha = entry.HasAlpha,
            Encoding = entry.Encoding,
        };
        WriteAtomic(texturePath + ".sc2tex.json", System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(metadata, true)));
    }
}
